using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Text.Json;
using Hydra.Shared;

namespace Hydra.Core;

public static class ChunkEngine
{
    public const int CHUNK_SIZE = 1 * 1024 * 1024;

    public static string ComputeHash(ReadOnlySpan<byte> data)
    {
        var hash = SHA256.HashData(data);
        return Convert.ToHexStringLower(hash);
    }

    public static List<ChunkRef> ChunkAndHash(byte[] data)
    {
        var chunks = new List<ChunkRef>();
        var offset = 0;
        var index = 0;

        while (offset < data.Length)
        {
            var size = Math.Min(CHUNK_SIZE, data.Length - offset);
            var slice = data.AsSpan(offset, size);
            var hash = ComputeHash(slice);
            chunks.Add(new ChunkRef(index, hash, size));
            offset += size;
            index++;
        }

        return chunks;
    }

    public static async Task<List<ChunkRef>> ChunkAndHashFromPipeAsync(
        PipeReader reader, long totalSize, Func<byte[], string, CancellationToken, Task> onChunk,
        CancellationToken ct)
    {
        var chunks = new List<ChunkRef>();
        var buffer = new byte[CHUNK_SIZE];
        var bufferPos = 0;
        long consumed = 0;

        while (consumed < totalSize)
        {
            var result = await reader.ReadAsync(ct);
            if (result.IsCanceled)
                throw new OperationCanceledException();

            var remaining = result.Buffer;
            foreach (var segment in remaining)
            {
                var segArray = segment.ToArray();
                var segOffset = 0;

                while (segOffset < segArray.Length && consumed < totalSize)
                {
                    var toCopy = Math.Min(segArray.Length - segOffset, CHUNK_SIZE - bufferPos);
                    toCopy = (int)Math.Min(toCopy, totalSize - consumed);

                    Array.Copy(segArray, segOffset, buffer, bufferPos, toCopy);
                    bufferPos += toCopy;
                    segOffset += toCopy;
                    consumed += toCopy;

                    if (bufferPos >= CHUNK_SIZE || consumed >= totalSize)
                    {
                        var chunkData = buffer[..bufferPos];
                        var hash = ComputeHash(chunkData);
                        var chunk = new ChunkRef(chunks.Count, hash, bufferPos);
                        chunks.Add(chunk);
                        await onChunk(chunkData, hash, ct);
                        bufferPos = 0;
                    }
                }
            }

            reader.AdvanceTo(remaining.End);
        }

        return chunks;
    }

    public static Manifest CreateManifest(string sessionId, int nPast, long totalSize, List<ChunkRef> chunks)
    {
        return new Manifest(
            SessionId: sessionId,
            Version: 1,
            NPast: nPast,
            TotalSize: totalSize,
            Chunks: chunks,
            CreatedAt: DateTime.UtcNow
        );
    }

    public static async Task<Manifest?> LoadManifestAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
            return null;

        var json = await File.ReadAllTextAsync(path, ct);
        return JsonSerializer.Deserialize<Manifest>(json);
    }

    public static async Task SaveManifestAsync(string path, Manifest manifest, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        var tmpPath = path + ".tmp";
        await File.WriteAllTextAsync(tmpPath, json, ct);
        File.Move(tmpPath, path, overwrite: true);
    }

    public static List<string> DiffPlan(Manifest manifest, List<string> clientHashes)
    {
        var clientSet = new HashSet<string>(clientHashes);
        return manifest.Chunks
            .Where(c => !clientSet.Contains(c.Hash))
            .Select(c => c.Hash)
            .ToList();
    }

    public static List<ChunkRef> DiffPlanWithInfo(Manifest manifest, List<string> clientHashes)
    {
        var clientSet = new HashSet<string>(clientHashes);
        return manifest.Chunks
            .Where(c => !clientSet.Contains(c.Hash))
            .ToList();
    }
}
