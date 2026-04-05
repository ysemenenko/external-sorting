namespace ExternalSorting.Core.IO;

/// <summary>Represents a temporary sorted chunk on disk.</summary>
public sealed class ChunkFile : IDisposable
{
    public string Path { get; }
    public int ItemCount { get; }

    public ChunkFile(string path, int itemCount)
    {
        Path = path;
        ItemCount = itemCount;
    }

    public void Dispose()
    {
        try { File.Delete(Path); } catch { /* best effort */ }
    }
}
