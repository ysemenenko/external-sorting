namespace ExternalSorting.Core.IO;

/// <summary>
/// A pass-through <see cref="Stream"/> wrapper that tallies how many bytes
/// were read from / written to the underlying stream. Used to populate
/// <c>SortMetrics.BytesRead</c> / <c>BytesWritten</c> with the real logical
/// I/O volume without the caller having to seek or measure stream length.
///
/// It never disposes the inner stream — the caller owns it.
/// </summary>
internal sealed class CountingStream : Stream
{
    private readonly Stream _inner;

    public CountingStream(Stream inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public long BytesRead { get; private set; }
    public long BytesWritten { get; private set; }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int n = _inner.Read(buffer, offset, count);
        BytesRead += n;
        return n;
    }

    public override int Read(Span<byte> buffer)
    {
        int n = _inner.Read(buffer);
        BytesRead += n;
        return n;
    }

    public override int ReadByte()
    {
        int b = _inner.ReadByte();
        if (b >= 0)
            BytesRead++;
        return b;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        _inner.Write(buffer, offset, count);
        BytesWritten += count;
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _inner.Write(buffer);
        BytesWritten += buffer.Length;
    }

    public override void WriteByte(byte value)
    {
        _inner.WriteByte(value);
        BytesWritten++;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override void Flush() => _inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => _inner.SetLength(value);

    // Deliberately does NOT dispose _inner — the caller owns the stream.
}
