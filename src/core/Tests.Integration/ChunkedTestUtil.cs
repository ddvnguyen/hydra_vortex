namespace Tests.Integration;

/// <summary>
/// Helpers for chunked-store tests. A GET_CHUNKED response payload is framed
/// per chunk as [4B index LE][4B size LE][body]; the real consumer
/// (StateHandler.RestoreFromStoreChunkedAsync) de-frames and reassembles it
/// by index. Tests must do the same instead of comparing raw bytes.
/// </summary>
internal static class ChunkedTestUtil
{
    /// <summary>
    /// De-frame a GET_CHUNKED payload and reassemble the original contiguous
    /// state. Chunks partition the state, so concatenating bodies in index
    /// order reproduces it exactly.
    /// </summary>
    public static byte[] Reassemble(byte[] framed)
    {
        var parsed = new List<(int Index, byte[] Body)>();
        int off = 0;
        while (off + 8 <= framed.Length)
        {
            int index = BitConverter.ToInt32(framed, off);
            int size = BitConverter.ToInt32(framed, off + 4);
            off += 8;
            if (size < 0 || off + size > framed.Length) break;
            var body = new byte[size];
            Array.Copy(framed, off, body, 0, size);
            off += size;
            parsed.Add((index, body));
        }

        return parsed
            .OrderBy(p => p.Index)
            .SelectMany(p => p.Body)
            .ToArray();
    }
}
