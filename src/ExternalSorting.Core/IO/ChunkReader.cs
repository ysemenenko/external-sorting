namespace ExternalSorting.Core.IO;

/// <summary>Reads items sequentially from a chunk file.</summary>
public sealed class ChunkReader<T> : IDisposable
{
    private readonly ISerializer<T> _serializer;
    private readonly FileStream _fs;
    private readonly BinaryReader _reader;
    private int _remaining;

    public bool HasMore => _remaining > 0;
    public T Current { get; private set; } = default!;

    public ChunkReader(ChunkFile chunk, ISerializer<T> serializer, int bufferSize = 64 * 1024)
    {
        _serializer = serializer;
        _fs = new FileStream(chunk.Path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize);
        _reader = new BinaryReader(_fs);
        _remaining = _reader.ReadInt32(); // header
    }

    /// <summary>Read next item into Current. Returns false when exhausted.</summary>
    public bool MoveNext()
    {
        if (_remaining <= 0)
            return false;

        Current = _serializer.Read(_reader);
        _remaining--;
        return true;
    }

    public void Dispose()
    {
        _reader.Dispose();
        _fs.Dispose();
    }
}
