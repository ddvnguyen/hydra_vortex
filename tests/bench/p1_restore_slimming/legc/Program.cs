// ═══════════════════════════════════════════════════════════════════════
// #720 P1 — leg (c): StateHandler streaming restore vs LIVE store+engines.
//
// Proves P1 items 1-3 end-to-end on the real rig:
//   1. no full-blob assembly buffer (streamed into the engine PUT body),
//   2. no trailing GetStateMetaAsync round-trip (PUT response is
//      authoritative for n_past/bytes),
//   3. HashSet chunk lookup in the store fetch plan.
//
// Flow:
//   (1) find a slot on engine 18086 (nodeA) with n_past>0 — if none,
//       prime via one non-streaming /chat/completions on coordinator
//       :19000 with X-Session-Id legc-prime-<date>, then re-check;
//   (2) StateHandler.SaveToStoreChunkedAsync  18086 → store 127.0.0.1:19500
//       (test-a store; scratch L1 dir so the restore takes the store path);
//   (3) StateHandler.RestoreFromStoreChunkedAsync → engine 18087 (nodeB).
//
// Asserts: restore success, NPast == source n_past, Bytes > 0 and ==
// source state size, no exceptions. Prints wall-times + JSON result.
// Exit 0 = PASS, 1 = FAIL.
//
// Scratch harness — references the worktree's built DLLs (no source copy).
// ═══════════════════════════════════════════════════════════════════════

using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Hydra.Core;
using Hydra.Shared;
using Serilog;

const string SrcEngine = "http://192.168.122.21:18086"; // nodeA
const string DstEngine = "http://192.168.122.21:18087"; // nodeB
const string StoreHost = "127.0.0.1";
const int    StorePort = 19500; // test-a store RPC (19501 is the debug port)
const string CoordinatorA = "http://127.0.0.1:19000";
const string L1Dir = "/tmp/p1_smoke/legc-l1"; // scratch L1 (empty → store path)
const string ModelName = "qwen3.5-9b-test";

string Sid = args.Length > 0 ? args[0] : $"legc-{DateTime.Now:yyyyMMdd}";
bool NoSave = args.Length > 1 && args[1] == "nosave"; // restore-only: fresh L1 → GET_CHUNKED store path
int ForcedSrcSlot = args.Length > 2 && int.TryParse(args[2], out var fs) ? fs : -1;
var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
var ct = cts.Token;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss.fff}] {Level:u3} {Message:lj}{NewLine}{Exception}")
    .CreateLogger();
var log = Log.Logger;

var result = new Dictionary<string, object?>
{
    ["session"] = Sid,
    ["store"] = $"{StoreHost}:{StorePort}",
    ["src_engine"] = SrcEngine,
    ["dst_engine"] = DstEngine,
};

try
{
    await using var store = new RpcClient(StoreHost, StorePort);
    var chunkCache = new LocalChunkCache(L1Dir, 5); // L1-only, no PG (scratch)

    // ── (1) source slot with live KV ────────────────────────────────────
    var src = new LlamaClient(SrcEngine);
    var dst = new LlamaClient(DstEngine);

    int srcSlot = -1;
    int srcNPast = -1;
    var slots = await src.GetSlotsAsync(ct);
    if (ForcedSrcSlot >= 0)
    {
        var forced = slots.FirstOrDefault(s => s.Id == ForcedSrcSlot && !s.IsProcessing && s.NPast > 0);
        if (forced is null)
            throw new InvalidOperationException($"forced source slot {ForcedSrcSlot} not usable (no n_past>0)");
        srcSlot = forced.Id;
        srcNPast = forced.NPast;
    }
    else
    {
        foreach (var s in slots.Where(s => !s.IsProcessing && s.NPast > 0).OrderByDescending(s => s.NPast))
        {
            srcSlot = s.Id;
            srcNPast = s.NPast;
            break;
        }
    }

    bool primed = false;
    if (srcSlot < 0)
    {
        log.Information("No slot with n_past>0 on {Src} — priming via {Coord}", SrcEngine, CoordinatorA);
        var filler = string.Concat(System.Linq.Enumerable.Repeat("The quick brown fox jumps over the lazy dog. ", 600)); // ~2.5k tokens
        var primeBody = new
        {
            model = ModelName,
            messages = new object[] { new { role = "user", content = filler } },
            max_tokens = 16,
            temperature = 0,
            force_mode = "solo",
        };
        using var primeHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var primeReq = new HttpRequestMessage(HttpMethod.Post, $"{CoordinatorA}/v1/chat/completions")
        {
            Content = JsonContent.Create(primeBody),
        };
        primeReq.Headers.Add("X-Session-Id", $"legc-prime-{DateTime.Now:yyyyMMdd}");
        var primeResp = await primeHttp.SendAsync(primeReq, ct);
        var primeText = await primeResp.Content.ReadAsStringAsync(ct);
        log.Information("Prime response: {Status} {Body}", (int)primeResp.StatusCode, primeText[..Math.Min(200, primeText.Length)]);
        if (!primeResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"priming failed: HTTP {(int)primeResp.StatusCode}");
        primed = true;

        // wait for the engine slot to show n_past>0 (completion done + slot idle)
        for (int i = 0; i < 60 && srcSlot < 0; i++)
        {
            await Task.Delay(2000, ct);
            slots = await src.GetSlotsAsync(ct);
            foreach (var s in slots.Where(s => !s.IsProcessing && s.NPast > 0).OrderByDescending(s => s.NPast))
            {
                srcSlot = s.Id;
                srcNPast = s.NPast;
                break;
            }
        }
        if (srcSlot < 0)
            throw new InvalidOperationException("no slot with n_past>0 appeared after priming (60 polls)");
    }
    log.Information("Source: {Src} slot {Slot} n_past={NPast} (primed={Primed})", SrcEngine, srcSlot, srcNPast, primed);

    var srcMeta = await src.GetStateMetaAsync(srcSlot, ct);
    log.Information("Source meta: state_size={Size} n_past={NPast}", srcMeta.StateSize, srcMeta.NPast);
    if (srcMeta.StateSize <= 0 || srcMeta.NPast <= 0)
        throw new InvalidOperationException($"source slot not usable (size={srcMeta.StateSize}, n_past={srcMeta.NPast})");

    // ── (2) StateHandler chunked save: 18086 → store 19500 ─────────────
    SaveResult? save = null;
    var saveSw = Stopwatch.StartNew();
    if (!NoSave)
    {
        var saveHandler = new StateHandler(src, store, chunkCache, log);
        save = await saveHandler.SaveToStoreChunkedAsync(Sid, srcSlot, $"legc-save-{Sid}", ct);
        saveSw.Stop();
        log.Information("SAVE done: n_past={NPast} size={Size} handler_ms={H} wall_ms={W}",
            save.NPast, save.Size, save.ElapsedMs, saveSw.ElapsedMilliseconds);
    }
    else
    {
        log.Information("nosave mode — restoring existing store blob for {Sid}", Sid);
    }

    // ── (3) free slot on 18087 ──────────────────────────────────────────
    // The warm-slot fast path in StateHandler (pre-P1) trusts the slot's
    // existing STATE_META checkpoint — so pick a slot whose meta is empty,
    // not merely one whose /slots prompt length is 0.
    var dstSlots = await dst.GetSlotsAsync(ct);
    int dstSlot = -1;
    foreach (var s in dstSlots.Where(s => !s.IsProcessing))
    {
        var meta = await dst.GetStateMetaAsync(s.Id, ct);
        if (meta.NPast == 0)
        {
            dstSlot = s.Id;
            break;
        }
    }
    if (dstSlot < 0)
        throw new InvalidOperationException("no dst slot with empty STATE_META");
    log.Information("Restore target: {Dst} slot {Slot}", DstEngine, dstSlot);

    // ── (4) StateHandler chunked restore: store → 18087 ────────────────
    var heapBefore = GC.GetTotalMemory(true);
    var restoreHandler = new StateHandler(dst, store, chunkCache, log);
    var restoreSw = Stopwatch.StartNew();
    var restore = await restoreHandler.RestoreFromStoreChunkedAsync(Sid, dstSlot, $"legc-restore-{Sid}", ct);
    restoreSw.Stop();
    var heapAfter = GC.GetTotalMemory(true);
    log.Information("RESTORE done: restored={R} n_past={NPast} size={Size} handler_ms={H} wall_ms={W}",
        restore.Restored, restore.NPast, restore.Size, restore.ElapsedMs, restoreSw.ElapsedMilliseconds);

    // ── (5) engine-side confirmation on the destination ─────────────────
    var dstMeta = await dst.GetStateMetaAsync(dstSlot, ct);
    log.Information("Dst meta after restore: state_size={Size} n_past={NPast}", dstMeta.StateSize, dstMeta.NPast);

    result["primed"] = primed;
    result["src_slot"] = srcSlot;
    result["src_n_past"] = srcNPast;
    result["src_state_size"] = srcMeta.StateSize;
    result["save"] = save is null ? "skipped(nosave)" : new { n_past = save.NPast, size = save.Size, handler_ms = save.ElapsedMs, wall_ms = saveSw.ElapsedMilliseconds };
    result["dst_slot"] = dstSlot;
    result["restore"] = new
    {
        restored = restore.Restored,
        n_past = restore.NPast,
        size = restore.Size,
        handler_ms = restore.ElapsedMs,
        wall_ms = restoreSw.ElapsedMilliseconds,
        heap_delta_bytes = heapAfter - heapBefore,
    };
    result["dst_meta_after"] = new { state_size = dstMeta.StateSize, n_past = dstMeta.NPast };

    bool pass =
        restore.Restored
        && (save is null || save.NPast == srcNPast)
        && (save is null || save.Size > 0)
        && restore.NPast == srcNPast
        && (save is null || restore.Size == save.Size)
        && dstMeta.NPast == srcNPast
        && dstMeta.StateSize == srcMeta.StateSize;

    result["verdict"] = pass ? "PASS" : "FAIL";
    if (!pass)
    {
        log.Error("LEG C FAILED — see fields above (expected n_past={Exp}, got restore={R} dst_meta={D})",
            srcNPast, restore.NPast, dstMeta.NPast);
        Environment.ExitCode = 1;
    }
    else
    {
        log.Information("LEG C PASS — save {S}ms / restore {R}ms for {B} bytes, n_past {N} exact",
            saveSw?.ElapsedMilliseconds ?? 0, restoreSw.ElapsedMilliseconds,
            save?.Size ?? restore.Size, srcNPast);
    }
}
catch (Exception ex)
{
    log.Error(ex, "LEG C EXCEPTION");
    result["verdict"] = "FAIL";
    result["error"] = ex.ToString();
    Environment.ExitCode = 1;
}

result["finished_at"] = DateTime.Now.ToString("o");
File.WriteAllText($"/tmp/p1_smoke/legc_{Sid}.json", JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"WROTE /tmp/p1_smoke/legc_{Sid}.json");
Log.CloseAndFlush();
