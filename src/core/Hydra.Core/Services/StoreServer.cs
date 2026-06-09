using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Hydra.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Prometheus;
using Serilog;
using Serilog.Context;

namespace Hydra.Core;

public sealed class StoreServer : RpcServer
{
	private readonly StoreConfig _cfg;
	private readonly StorageEngine _engine;
	private readonly ChunkStore _chunkStore;
	private readonly StoreMetadata? _metadata;
	private static readonly Serilog.ILogger _log = Serilog.Log.ForContext<StoreServer>();

	public StoreServer(StoreConfig cfg, StorageEngine engine, ChunkStore chunkStore, StoreMetadata? metadata = null)
		 : base(cfg.Host, cfg.Port)
	{
		_cfg = cfg;
		_engine = engine;
		_chunkStore = chunkStore;
		_metadata = metadata;
	}

	private StoreMetadata Metadata => _metadata ?? throw new InvalidOperationException(
		"StoreMetadata not configured: metadata-dependent operations require Postgres");

	protected override async Task HandleAsync(
		OpCode op, string key, string traceId, long payloadLen,
		PipeReader reader, PipeWriter writer, TcpClient client, CancellationToken ct)
	{
		using var _ = _log.TraceScope(traceId);

		switch (op)
		{
			case OpCode.Put:
				await HandlePutAsync(key, payloadLen, reader, writer, ct);
				break;
			case OpCode.Get:
				await HandleGetAsync(key, writer, client, ct);
				break;
			case OpCode.Del:
				await HandleDelAsync(key, writer, ct);
				break;
			case OpCode.Stat:
				await HandleStatAsync(key, writer, ct);
				break;
			case OpCode.List:
				await HandleListAsync(key, writer, ct);
				break;
			case OpCode.PutChunked:
				await HandlePutChunkedAsync(key, payloadLen, reader, writer, ct);
				break;
			case OpCode.GetChunked:
				await HandleGetChunkedAsync(key, payloadLen, reader, writer, client, ct);
				break;
			case OpCode.GetManifest:
				await HandleGetManifestAsync(key, writer, ct);
				break;
			case OpCode.SyncMissing:
				await HandleSyncMissingAsync(key, payloadLen, reader, writer, ct);
				break;
			case OpCode.PushChunks:
				await HandlePushChunksAsync(key, payloadLen, reader, writer, ct);
				break;
			case OpCode.PutManifest:
				await HandlePutManifestAsync(key, payloadLen, reader, writer, ct);
				break;
			case OpCode.PutMeta:
				await HandlePutMetaAsync(key, payloadLen, reader, writer, ct);
				break;
			default:
				await WriteErrorAsync(writer, $"Unknown opcode: {op}", ct);
				break;
		}
	}

	private async Task HandlePutAsync(
		string key, long payloadLen, PipeReader reader, PipeWriter writer, CancellationToken ct)
	{
		using var _ = StoreMetrics.OpDuration.WithLabels("put").NewTimer();
		try
		{
			await _engine.PutAsync(key, reader, payloadLen, ct);
			StoreMetrics.OpsTotal.WithLabels("put").Inc();
			StoreMetrics.BytesStored.Inc(payloadLen);

			var meta = """{"stored":true}""";
			var metaBytes = Encoding.UTF8.GetBytes(meta);
			await WriteResponseHeaderAsync(writer, (byte)StatusCode.Ok, (uint)metaBytes.Length, 0, ct);
			await WriteMetaAsync(writer, meta, ct);
		}
		catch (Exception ex)
		{
			await WriteErrorAsync(writer, ex.Message, ct);
		}
	}

	private async Task HandleGetAsync(
		string key, PipeWriter writer, TcpClient client, CancellationToken ct)
	{
		using var _ = StoreMetrics.OpDuration.WithLabels("get").NewTimer();
		var file = await _engine.GetAsync(key, ct);
		if (file is null)
		{
			StoreMetrics.OpsTotal.WithLabels("get_not_found").Inc();
			await WriteErrorAsync(writer, "not_found", ct, StatusCode.NotFound);
			return;
		}

		var meta = $$"""{"size":{{file.Length}}}""";
		var metaBytes = Encoding.UTF8.GetBytes(meta);

		await WriteResponseHeaderAsync(writer, (byte)StatusCode.Ok, (uint)metaBytes.Length, (ulong)file.Length, ct);
		await WriteMetaAsync(writer, meta, ct);

		await SendFileAsync(client, file.FullName, ct);
		StoreMetrics.OpsTotal.WithLabels("get").Inc();
		StoreMetrics.BytesSent.Inc(file.Length);
	}

	private async Task HandleDelAsync(string key, PipeWriter writer, CancellationToken ct)
	{
		using var _ = StoreMetrics.OpDuration.WithLabels("del").NewTimer();
		var deleted = await _engine.DeleteAsync(key, ct);
		var meta = deleted ? """{"deleted":true}""" : """{"deleted":false}""";
		var metaBytes = Encoding.UTF8.GetBytes(meta);
		await WriteResponseHeaderAsync(writer, (byte)StatusCode.Ok, (uint)metaBytes.Length, 0, ct);
		await WriteMetaAsync(writer, meta, ct);
		StoreMetrics.OpsTotal.WithLabels("del").Inc(deleted ? 1 : 0);
	}

	private async Task HandleStatAsync(string key, PipeWriter writer, CancellationToken ct)
	{
		using var _ = StoreMetrics.OpDuration.WithLabels("stat").NewTimer();
		var stat = await _engine.StatAsync(key, ct);
		if (stat is null)
		{
			StoreMetrics.OpsTotal.WithLabels("stat_not_found").Inc();
			await WriteErrorAsync(writer, "not_found", ct, StatusCode.NotFound);
			return;
		}

		var meta = $$"""{"name":"{{stat.Name}}","size":{{stat.Size}},"modified":"{{stat.LastModified:O}}"}""";
		var metaBytes = Encoding.UTF8.GetBytes(meta);
		await WriteResponseHeaderAsync(writer, (byte)StatusCode.Ok, (uint)metaBytes.Length, 0, ct);
		await WriteMetaAsync(writer, meta, ct);
		StoreMetrics.OpsTotal.WithLabels("stat").Inc();
	}

	private async Task HandleListAsync(string prefix, PipeWriter writer, CancellationToken ct)
	{
		using var _ = StoreMetrics.OpDuration.WithLabels("list").NewTimer();
		var files = new List<string>();
		await foreach (var f in _engine.ListAsync(prefix, ct))
			files.Add(f);
		StoreMetrics.OpsTotal.WithLabels("list").Inc();

		var payload = JsonSerializer.SerializeToUtf8Bytes(files);
		var meta = $$"""{"count":{{files.Count}}}""";
		var metaBytes = Encoding.UTF8.GetBytes(meta);

		await WriteResponseHeaderAsync(writer, (byte)StatusCode.Ok, (uint)metaBytes.Length, (ulong)payload.Length, ct);
		await WriteMetaAsync(writer, meta, ct);
		await writer.WriteAsync(payload, ct);
	}

  private async Task HandlePutChunkedAsync(
		 string key, long payloadLen, PipeReader reader, PipeWriter writer, CancellationToken ct)
	{
		var nodeLabel = _cfg.NodeName;
		string sizeBucket = payloadLen switch
		{
			< 1_000_000 => "0-1MB",
			< 5_000_000 => "1-5MB",
			< 10_000_000 => "5-10MB",
			_ => "10+MB"
		};

		using var _ = StoreMetrics.ChunkOpDuration.WithLabels("put_chunked", nodeLabel).NewTimer();

		try
		{
			var chunks = new List<ChunkRef>();
			var totalNew = 0;

 		await ChunkEngine.ChunkAndHashFromPipeAsync(reader, payloadLen,
 				 async (chunkData, hash, innerCt) =>
 				 {
 					 var chunkRef = new ChunkRef(chunks.Count, hash, chunkData.Length);
 					 chunks.Add(chunkRef);

 					 if (await _chunkStore.StoreChunkAsync(hash, chunkData, innerCt))
 					 {
 						 await Metadata.RegisterChunkAsync(hash, chunkData.Length, innerCt);
 						 Interlocked.Increment(ref totalNew);
 					 }
 				 }, ct);

			var totalChunks = chunks.Count;
			var deduped = totalChunks - totalNew;

			var storedNpast = await Metadata.GetNPastAsync(key, ct);
			var nPast = storedNpast ?? 0;
			await Metadata.SetNPastAsync(key, 0, ct);

			await Metadata.UpsertManifestAsync(key, nPast, payloadLen, chunks, ct);

			StoreMetrics.OpsTotal.WithLabels("put_chunked").Inc();
			StoreMetrics.BytesStored.Inc(payloadLen);
			StoreMetrics.ChunksNew.Inc(totalNew);
			StoreMetrics.ChunksDeduped.Inc(deduped);
			StoreMetrics.KVPutBytesTotal.WithLabels(nodeLabel, "put_chunked").Inc(payloadLen);
			StoreMetrics.ChunkCount.WithLabels("put_chunked", sizeBucket).Observe(totalChunks);

			var meta = $$"""{"new_chunks":{{totalNew}},"deduped_chunks":{{deduped}},"total_chunks":{{totalChunks}}}""";
			var metaBytes = Encoding.UTF8.GetBytes(meta);
			await WriteResponseHeaderAsync(writer, (byte)StatusCode.Ok, (uint)metaBytes.Length, 0, ct);
			await WriteMetaAsync(writer, meta, ct);
		}
		catch (Exception ex)
		{
			await WriteErrorAsync(writer, $"put_chunked failed: {ex.Message}", ct);
		}
	}

 private async Task HandleGetChunkedAsync(
		 string key, long payloadLen, PipeReader reader, PipeWriter writer, TcpClient client, CancellationToken ct)
	{
		var nodeLabel = _cfg.NodeName;

		using var _ = StoreMetrics.ChunkOpDuration.WithLabels("get_chunked", nodeLabel).NewTimer();

		try
		{
			var manifest = await Metadata.GetManifestAsync(key, ct);
			if (manifest is null)
			{
				StoreMetrics.OpsTotal.WithLabels("get_chunked_not_found").Inc();
				await WriteErrorAsync(writer, "not_found", ct, StatusCode.NotFound);
				return;
			}

			List<string> knownHashes = [];
			if (payloadLen > 0)
			{
				var clientHashesJson = await ReadPayloadAsync(reader, payloadLen, ct);
				knownHashes = JsonSerializer.Deserialize<List<string>>(clientHashesJson) ?? [];
			}

			var missingChunks = ChunkEngine.DiffPlanWithInfo(manifest, knownHashes);

			// Resolve once so the declared payload length exactly matches what we write:
			// each resident chunk contributes 8 bytes of framing ([4B index][4B size])
			// plus its body. (Previously total_size counted bodies only, so the client
			// read a stream short by 8 bytes per chunk and dropped the tail — corrupting
			// restore.)
			var resident = new List<(ChunkRef Chunk, string Path, long Length)>();
			long bodyBytes = 0;
			foreach (var mc in missingChunks)
			{
				var path = _chunkStore.GetChunkPath(mc.Hash);
				if (path is null) continue;
				try
				{
					var len = new FileInfo(path).Length;
					resident.Add((mc, path, len));
					bodyBytes += len;
				}
				catch (FileNotFoundException)
				{
					// GC removed this chunk between existence check and read — skip
				}
			}
			ulong framedLen = (ulong)bodyBytes + (ulong)(resident.Count * 8);

			string sizeBucket = resident.Count switch
			{
				0 => "0-chunks",
				< 5 => "1-4",
				< 20 => "5-19",
				_ => "20+"
			};

			// total_size in meta stays bodies-only (what the chunks actually weigh);
			// payload_len on the wire includes the per-chunk framing.
			var meta = $$"""{"missing_count":{{resident.Count}},"total_size":{{bodyBytes}}}""";
			var metaBytes = Encoding.UTF8.GetBytes(meta);

			await WriteResponseHeaderAsync(writer, (byte)StatusCode.Ok, (uint)metaBytes.Length, framedLen, ct);
			await WriteMetaAsync(writer, meta, ct);

			// Write framing [4B index][4B size], flush, then sendfile to avoid
			// reading the entire chunk into heap before sending.
			foreach (var (mc, path, _) in resident)
			{
				var header = new byte[8];
				BinaryPrimitives.WriteInt32LittleEndian(header, mc.Index);
				BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), mc.Size);
				await writer.WriteAsync(header, ct);
				await writer.FlushAsync(ct);
				await SendFileAsync(client, path, ct);
			}

			StoreMetrics.OpsTotal.WithLabels("get_chunked").Inc();
			StoreMetrics.BytesSent.Inc(bodyBytes);
			StoreMetrics.ChunksSent.Inc(resident.Count);
			StoreMetrics.KVGetBytesTotal.WithLabels(nodeLabel, "get_chunked").Inc(bodyBytes);
			StoreMetrics.ChunkCount.WithLabels("get_chunked", sizeBucket).Observe(resident.Count);
		}
		catch (Exception ex)
		{
			await WriteErrorAsync(writer, $"get_chunked failed: {ex.Message}", ct);
		}
	}

	// Delta-save step 1: of the hashes the client intends to store, return the subset
	// the global chunk index does NOT already have. No manifest required (save direction).
	// Request payload: JSON ["hash", ...]   Response payload: JSON {"missing_hashes":[...]}.
	private async Task HandleSyncMissingAsync(
		 string key, long payloadLen, PipeReader reader, PipeWriter writer, CancellationToken ct)
	{
		var nodeLabel = _cfg.NodeName;

		using var _ = StoreMetrics.ChunkOpDuration.WithLabels("sync_missing", nodeLabel).NewTimer();

		try
		{
			List<string> candidateHashes = [];
			if (payloadLen > 0)
			{
				var json = await ReadPayloadAsync(reader, payloadLen, ct);
				candidateHashes = JsonSerializer.Deserialize<List<string>>(json) ?? [];
			}

			var missingHashes = new List<string>();
			foreach (var h in candidateHashes.Distinct())
			{
				if (_chunkStore.HasChunk(h))
				{
					// On disk but potentially absent from PG — backfill to keep PG consistent.
					if (!await Metadata.HasChunkAsync(h, ct))
					{
						var path = _chunkStore.GetChunkPath(h);
						var size = path is not null ? new FileInfo(path).Length : 0;
						await Metadata.RegisterChunkAsync(h, (int)size, ct);
						_log.Debug("SyncMissing: backfilled PG row for on-disk chunk {Hash}", h);
					}
					continue;
				}
				if (!await Metadata.HasChunkAsync(h, ct))
				{
					missingHashes.Add(h);
				}
				else
				{
					_log.Warning("SyncMissing: hash {Hash} exists in PG but missing from disk — deleting stale PG row", h);
					await Metadata.DeleteChunkAsync(h, ct);
					missingHashes.Add(h);
				}
			}

			var payload = JsonSerializer.SerializeToUtf8Bytes(new { missing_hashes = missingHashes });
			var meta = $$"""{"missing_count":{{missingHashes.Count}},"candidate_count":{{candidateHashes.Count}}}""";
			var metaBytes = Encoding.UTF8.GetBytes(meta);

			await WriteResponseHeaderAsync(writer, (byte)StatusCode.Ok, (uint)metaBytes.Length, (ulong)payload.Length, ct);
			await WriteMetaAsync(writer, meta, ct);
			await writer.WriteAsync(payload, ct);

			StoreMetrics.OpsTotal.WithLabels("sync_missing").Inc();
			StoreMetrics.SyncCandidateCount.WithLabels(nodeLabel, "candidate").Set(candidateHashes.Count);
			StoreMetrics.SyncMissingCount.WithLabels(nodeLabel, "missing").Set(missingHashes.Count);
		}
		catch (Exception ex)
		{
			await WriteErrorAsync(writer, $"sync_missing failed: {ex.Message}", ct);
		}
	}

	// Delta-save step 2: store the chunk bodies the client was told are missing. Pure
	// blob writes (content-addressed dedup) — does NOT touch the manifest. The manifest
	// is written authoritatively by PUT_MANIFEST, so partial pushes can never corrupt it.
	// Request payload: [4B size LE][body] ...
	private async Task HandlePushChunksAsync(
		 string key, long payloadLen, PipeReader reader, PipeWriter writer, CancellationToken ct)
	{
		var nodeLabel = _cfg.NodeName;

		using var _ = StoreMetrics.ChunkOpDuration.WithLabels("push_chunks", nodeLabel).NewTimer();

		try
		{
			if (payloadLen > _cfg.MaxPayloadBytes)
			{
				await WriteErrorAsync(writer, $"payload exceeds max allowed size ({_cfg.MaxPayloadBytes} bytes)", ct, StatusCode.BadRequest);
				return;
			}

			var pushedBytes = await ReadPayloadAsync(reader, payloadLen, ct);
			var offset = 0;
			var stored = 0;
			var received = 0;
			var totalPushedSize = 0L;

			while (offset < pushedBytes.Length)
			{
				if (offset + 4 > pushedBytes.Length) break;
				var chunkSize = BinaryPrimitives.ReadInt32LittleEndian(pushedBytes.AsSpan(offset));
				offset += 4;

				if (chunkSize <= 0 || offset + chunkSize > pushedBytes.Length) break;
				var chunkData = pushedBytes.AsSpan(offset, chunkSize).ToArray();
				offset += chunkSize;

				var hash = ChunkEngine.ComputeHash(chunkData);
				received++;
				totalPushedSize += chunkSize;
				if (await _chunkStore.StoreChunkAsync(hash, chunkData, ct))
					stored++;
				// Register in PG for EVERY pushed chunk, not only newly-written files: a body that
				// already exists on disk but lacks a PG row would otherwise stay unregistered and
				// later fail the session_chunks FK (#138). Idempotent (ON CONFLICT DO NOTHING).
				await Metadata.RegisterChunkAsync(hash, chunkSize, ct);
			}

			var meta = $$"""{"stored":{{stored}},"received":{{received}}}""";
			var metaBytes = Encoding.UTF8.GetBytes(meta);
			await WriteResponseHeaderAsync(writer, (byte)StatusCode.Ok, (uint)metaBytes.Length, 0, ct);
			await WriteMetaAsync(writer, meta, ct);

			StoreMetrics.OpsTotal.WithLabels("push_chunks").Inc();
			StoreMetrics.BytesStored.Inc(totalPushedSize);
			StoreMetrics.KVPutBytesTotal.WithLabels(nodeLabel, "push_chunks").Inc(totalPushedSize);
			StoreMetrics.ChunkCount.WithLabels("push_chunks", stored switch { 0 => "0-chunks", < 5 => "1-4", < 20 => "5-19", _ => "20+" }).Observe(stored);
		}
		catch (Exception ex)
		{
			await WriteErrorAsync(writer, $"push_chunks failed: {ex.Message}", ct);
		}
	}

	// Delta-save step 3: write the authoritative ordered manifest. Every referenced chunk
	// must already be resident (pushed now or deduped from a prior save); if any is missing
	// we refuse rather than write a manifest that would reconstruct to garbage on restore.
	// Request payload: JSON {"n_past":N,"total_size":T,"chunks":[{"index":i,"hash":h,"size":s},...]}.
	private async Task HandlePutManifestAsync(
		 string key, long payloadLen, PipeReader reader, PipeWriter writer, CancellationToken ct)
	{
		using var _ = StoreMetrics.OpDuration.WithLabels("put_manifest").NewTimer();

		try
		{
			if (payloadLen <= 0)
			{
				await WriteErrorAsync(writer, "empty manifest", ct, StatusCode.BadRequest);
				return;
			}

			var json = await ReadPayloadAsync(reader, payloadLen, ct);
			using var doc = JsonDocument.Parse(json);
			var root = doc.RootElement;

			int nPast = root.TryGetProperty("n_past", out var np) ? np.GetInt32() : 0;
			long totalSize = root.TryGetProperty("total_size", out var ts) ? ts.GetInt64() : 0;

			var chunks = new List<ChunkRef>();
			var missing = new List<string>();
			if (root.TryGetProperty("chunks", out var chunksEl) && chunksEl.ValueKind == JsonValueKind.Array)
			{
				foreach (var c in chunksEl.EnumerateArray())
				{
					var idx = c.GetProperty("index").GetInt32();
					var hash = c.GetProperty("hash").GetString() ?? "";
					var size = c.GetProperty("size").GetInt32();
					chunks.Add(new ChunkRef(idx, hash, size));
					if (!_chunkStore.HasChunk(hash) && !await Metadata.HasChunkAsync(hash, ct))
						missing.Add(hash);
				}
			}

			if (missing.Count > 0)
			{
				StoreMetrics.OpsTotal.WithLabels("put_manifest_missing_chunks").Inc();
				var errPayload = JsonSerializer.SerializeToUtf8Bytes(new { missing_hashes = missing });
				var errMeta = $$"""{"error":"manifest references {{missing.Count}} unresident chunks"}""";
				var errMetaBytes = Encoding.UTF8.GetBytes(errMeta);
				await WriteResponseHeaderAsync(writer, (byte)StatusCode.Partial, (uint)errMetaBytes.Length, (ulong)errPayload.Length, ct);
				await WriteMetaAsync(writer, errMeta, ct);
				await writer.WriteAsync(errPayload, ct);
				return;
			}

			chunks.Sort((a, b) => a.Index.CompareTo(b.Index));
			await Metadata.UpsertManifestAsync(key, nPast, totalSize, chunks, ct);

			var meta = $$"""{"written":true,"chunks":{{chunks.Count}},"n_past":{{nPast}}}""";
			var metaBytes = Encoding.UTF8.GetBytes(meta);
			await WriteResponseHeaderAsync(writer, (byte)StatusCode.Ok, (uint)metaBytes.Length, 0, ct);
			await WriteMetaAsync(writer, meta, ct);

			StoreMetrics.OpsTotal.WithLabels("put_manifest").Inc();
		}
		catch (Exception ex)
		{
			await WriteErrorAsync(writer, $"put_manifest failed: {ex.Message}", ct);
		}
	}

	private async Task HandleGetManifestAsync(string key, PipeWriter writer, CancellationToken ct)
	{
		using var _ = StoreMetrics.OpDuration.WithLabels("get_manifest").NewTimer();

		try
		{
			var manifest = await Metadata.GetManifestAsync(key, ct);
			if (manifest is null)
			{
				StoreMetrics.OpsTotal.WithLabels("get_manifest_not_found").Inc();
				await WriteErrorAsync(writer, "not_found", ct, StatusCode.NotFound);
				return;
			}

			var payload = JsonSerializer.SerializeToUtf8Bytes(new {
				n_past = manifest.NPast,
				total_size = manifest.TotalSize,
				chunks = manifest.Chunks.Select(c => new { index = c.Index, hash = c.Hash, size = c.Size })
			});

			var meta = $$"""{"chunk_count":{{manifest.Chunks.Count}}}""";
			var metaBytes = Encoding.UTF8.GetBytes(meta);

			await WriteResponseHeaderAsync(writer, (byte)StatusCode.Ok, (uint)metaBytes.Length, (ulong)payload.Length, ct);
			await WriteMetaAsync(writer, meta, ct);
			await writer.WriteAsync(payload, ct);
			await writer.FlushAsync(ct);

			StoreMetrics.OpsTotal.WithLabels("get_manifest").Inc();
		}
		catch (Exception ex)
		{
			await WriteErrorAsync(writer, $"get_manifest failed: {ex.Message}", ct);
		}
	}

	private async Task HandlePutMetaAsync(string key, long payloadLen, PipeReader reader, PipeWriter writer, CancellationToken ct)
	{
		using var _ = StoreMetrics.OpDuration.WithLabels("put_meta").NewTimer();

		try
		{
			var payload = await ReadPayloadAsync(reader, payloadLen, ct);

			if (payload.Length == 0)
			{
				await WriteErrorAsync(writer, "empty payload", ct, StatusCode.BadRequest);
				return;
			}

			using var doc = System.Text.Json.JsonDocument.Parse(payload);
			if (doc.RootElement.TryGetProperty("n_past", out var npEl))
			{
				int nPast = npEl.GetInt32();
				await Metadata.SetNPastAsync(key, nPast, ct);

				var meta = $$"""{"n_past":{{nPast}}}""";
				var metaBytes = Encoding.UTF8.GetBytes(meta);
				await WriteResponseHeaderAsync(writer, (byte)StatusCode.Ok, (uint)metaBytes.Length, 0, ct);
				await WriteMetaAsync(writer, meta, ct);
			}
			else
			{
				var meta = """{"n_past":null}"""u8.ToArray();
				await WriteResponseHeaderAsync(writer, (byte)StatusCode.Ok, (uint)meta.Length, 0, ct);
				await writer.WriteAsync(meta, ct);
			}

			StoreMetrics.OpsTotal.WithLabels("put_meta").Inc();
		}
		catch (Exception ex)
		{
			await WriteErrorAsync(writer, $"put_meta failed: {ex.Message}", ct);
		}
	}

	private async Task<int> RunGCAsync(CancellationToken ct)
	{
		var removed = await Metadata.GcOrphanChunksAsync(_chunkStore.ChunksDirectory, ct);
		if (removed > 0)
			StoreMetrics.ChunksRemoved.Inc(removed);
		return removed;
	}

	private static async Task WriteErrorAsync(PipeWriter writer, string message, CancellationToken ct, StatusCode status = StatusCode.Error)
	{
		var meta = JsonSerializer.Serialize(new { error = message });
		var metaBytes = Encoding.UTF8.GetBytes(meta);
		await WriteResponseHeaderAsync(writer, (byte)status, (uint)metaBytes.Length, 0, ct);
		await WriteMetaAsync(writer, meta, ct);
	}

	public Task StartDebugEndpointAsync(CancellationToken ct)
	{
		var builder = WebApplication.CreateBuilder();
		builder.WebHost.UseSetting(WebHostDefaults.ServerUrlsKey, $"http://{_cfg.Host}:{_cfg.DebugHttpPort}");

		var app = builder.Build();

		app.MapGet("/version", () => Results.Json(new
		{
			service = "hydra-store",
			version = HydraLogging.ServiceVersion,
		}));

		app.MapGet("/debug", async (HttpContext ctx) =>
		{
			var storeStats = await _engine.GetDebugStatsAsync(ct);
			var chunkStats = await _chunkStore.GetStatsAsync(ct);
			var pgStats = _metadata is not null
				? await _metadata.GetStatsAsync(ct)
				: (ManifestCount: 0, ChunkRows: 0);
			var resp = new Dictionary<string, object>
			{
				["version"] = HydraLogging.ServiceVersion,
				["raw"] = storeStats,
				["chunks"] = new { chunkStats.TotalChunks, pgStats.ManifestCount, chunkStats.TotalBytes },
			};
			if (_metadata is not null)
			{
				var ids = await _metadata.GetRecentSessionIdsAsync(200, CancellationToken.None);
				var list = new List<object>();
				foreach (var id in ids)
				{
					var m = await _metadata.GetManifestAsync(id, CancellationToken.None);
					if (m is not null)
						list.Add(new { session_id = id, n_past = m.NPast, total_size = m.TotalSize });
				}
				resp["sessions"] = list;
			}
			return Results.Json(resp);
		});

		app.MapPost("/debug/gc", async () =>
		{
			if (_metadata is null)
				return new { chunks_removed = 0 };
			var removed = await _metadata.GcOrphanChunksAsync(_chunkStore.ChunksDirectory, CancellationToken.None);
			if (removed > 0)
				StoreMetrics.ChunksRemoved.Inc(removed);
			return new { chunks_removed = removed };
		});

		app.UseMetricServer();

		ct.Register(async () => await app.StopAsync());
		return app.RunAsync();
	}
}


