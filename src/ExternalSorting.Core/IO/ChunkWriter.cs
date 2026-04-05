namespace ExternalSorting.Core.IO;

/// <summary>Writes sorted items to a chunk file using binary serialization.</summary>
public static class ChunkWriter
{
    public static ChunkFile Write<T>(
        IReadOnlyList<T> items,
        ISerializer<T> serializer,
        string directory,
        int chunkIndex,
        int bufferSize = 64 * 1024)
    {
        string path = Path.Combine(directory, $"chunk_{chunkIndex:D6}.bin");

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize);
        using var bw = new BinaryWriter(fs);

        // Header: item count
        bw.Write(items.Count);

        for (int i = 0; i < items.Count; i++)
            serializer.Write(bw, items[i]);

        return new ChunkFile(path, items.Count);
    }
}
