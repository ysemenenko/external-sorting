using ExternalSorting.Core.IO;
using FluentAssertions;

namespace ExternalSorting.Tests;

public class ChunkIOTests : IDisposable
{
    private readonly string _tempDir;
    private readonly RecordSerializer _serializer = new();

    public ChunkIOTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"chunk_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public void Write_and_read_roundtrip()
    {
        var items = new SortRecord[]
        {
            new(1, "Apple"),
            new(2, "Banana"),
            new(3, "Cherry"),
        };

        var chunk = ChunkWriter.Write(items, _serializer, _tempDir, 0);
        chunk.ItemCount.Should().Be(3);
        File.Exists(chunk.Path).Should().BeTrue();

        using var reader = new ChunkReader<SortRecord>(chunk, _serializer);
        var result = new List<SortRecord>();
        while (reader.MoveNext())
            result.Add(reader.Current);

        result.Should().Equal(items);
    }

    [Fact]
    public void Empty_chunk()
    {
        var chunk = ChunkWriter.Write(Array.Empty<SortRecord>(), _serializer, _tempDir, 0);
        chunk.ItemCount.Should().Be(0);

        using var reader = new ChunkReader<SortRecord>(chunk, _serializer);
        reader.HasMore.Should().BeFalse();
        reader.MoveNext().Should().BeFalse();
    }

    [Fact]
    public void ChunkFile_dispose_deletes_file()
    {
        var chunk = ChunkWriter.Write(
            new[] { new SortRecord(1, "test") }, _serializer, _tempDir, 0);

        string path = chunk.Path;
        File.Exists(path).Should().BeTrue();

        chunk.Dispose();
        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public void Large_chunk_roundtrip()
    {
        var rng = new Random(42);
        var items = Enumerable.Range(0, 10_000)
            .Select(i => new SortRecord((ulong)rng.NextInt64(), $"word_{i % 100}"))
            .ToArray();

        var chunk = ChunkWriter.Write(items, _serializer, _tempDir, 0);

        using var reader = new ChunkReader<SortRecord>(chunk, _serializer);
        int count = 0;
        while (reader.MoveNext())
        {
            reader.Current.Should().Be(items[count]);
            count++;
        }

        count.Should().Be(items.Length);
    }
}
