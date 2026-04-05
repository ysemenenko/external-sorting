using ExternalSorting.Core;
using ExternalSorting.Core.IO;
using ExternalSorting.Core.Pipeline;
using FluentAssertions;

namespace ExternalSorting.Tests;

public class ExternalSorterTests : IDisposable
{
    private readonly string _tempDir;
    private readonly RecordSerializer _serializer = new();

    public ExternalSorterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sort_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private ExternalSorter<SortRecord> CreateSorter(long memoryBytes = 4096, int mergeWay = 2)
    {
        return new ExternalSorter<SortRecord>(_serializer, Comparer<SortRecord>.Default, new SortOptions
        {
            MaxMemoryBytes = memoryBytes,
            MergeWayCount = mergeWay,
            TempDirectory = _tempDir,
        });
    }

    private MemoryStream WriteRecords(params SortRecord[] records)
    {
        var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        foreach (var r in records)
            _serializer.Write(bw, r);
        ms.Position = 0;
        return ms;
    }

    private List<SortRecord> ReadOutput(MemoryStream output)
    {
        output.Position = 0;
        var br = new BinaryReader(output);
        int count = br.ReadInt32();
        var result = new List<SortRecord>(count);
        for (int i = 0; i < count; i++)
            result.Add(_serializer.Read(br));
        return result;
    }

    [Fact]
    public void Sort_empty_input()
    {
        var sorter = CreateSorter();
        using var input = new MemoryStream();
        using var output = new MemoryStream();

        sorter.Sort(input, output);

        output.Length.Should().Be(0);
    }

    [Fact]
    public void Sort_single_item()
    {
        var sorter = CreateSorter();
        using var input = WriteRecords(new SortRecord(1, "Apple"));
        using var output = new MemoryStream();

        sorter.Sort(input, output);

        var result = ReadOutput(output);
        result.Should().HaveCount(1);
        result[0].Should().Be(new SortRecord(1, "Apple"));
    }

    [Fact]
    public void Sort_already_sorted()
    {
        var records = new[]
        {
            new SortRecord(1, "Apple"),
            new SortRecord(2, "Banana"),
            new SortRecord(3, "Cherry"),
        };

        var sorter = CreateSorter();
        using var input = WriteRecords(records);
        using var output = new MemoryStream();

        sorter.Sort(input, output);

        var result = ReadOutput(output);
        result.Should().Equal(records);
    }

    [Fact]
    public void Sort_reverse_order()
    {
        var records = new[]
        {
            new SortRecord(3, "Cherry"),
            new SortRecord(2, "Banana"),
            new SortRecord(1, "Apple"),
        };

        var sorter = CreateSorter();
        using var input = WriteRecords(records);
        using var output = new MemoryStream();

        sorter.Sort(input, output);

        var result = ReadOutput(output);
        result.Should().BeInAscendingOrder(r => r.Text);
    }

    [Fact]
    public void Sort_with_duplicates()
    {
        var records = new[]
        {
            new SortRecord(2, "Apple"),
            new SortRecord(1, "Apple"),
            new SortRecord(3, "Apple"),
        };

        var sorter = CreateSorter();
        using var input = WriteRecords(records);
        using var output = new MemoryStream();

        sorter.Sort(input, output);

        var result = ReadOutput(output);
        result.Should().HaveCount(3);
        // Same text, sorted by number
        result[0].Number.Should().Be(1);
        result[1].Number.Should().Be(2);
        result[2].Number.Should().Be(3);
    }

    [Fact]
    public void Sort_forces_multiple_chunks()
    {
        // Small memory = 1 item per chunk → forces merge
        var records = new[]
        {
            new SortRecord(5, "Elderberry"),
            new SortRecord(3, "Cherry"),
            new SortRecord(1, "Apple"),
            new SortRecord(4, "Date"),
            new SortRecord(2, "Banana"),
        };

        var sorter = CreateSorter(memoryBytes: 48); // ~1 item per chunk
        using var input = WriteRecords(records);
        using var output = new MemoryStream();

        sorter.Sort(input, output);

        var result = ReadOutput(output);
        result.Should().HaveCount(5);
        result.Should().BeInAscendingOrder(r => r);
        sorter.LastMetrics!.ChunksCreated.Should().BeGreaterThan(1);
    }

    [Fact]
    public void Sort_forces_multiple_merge_passes()
    {
        // 2-way merge with many chunks = multiple passes
        var rng = new Random(42);
        var records = Enumerable.Range(0, 100)
            .Select(_ => new SortRecord((ulong)rng.NextInt64(0, 1000), $"w{rng.Next(20)}"))
            .ToArray();

        var sorter = CreateSorter(memoryBytes: 48, mergeWay: 2);
        using var input = WriteRecords(records);
        using var output = new MemoryStream();

        sorter.Sort(input, output);

        var result = ReadOutput(output);
        result.Should().HaveCount(records.Length);

        // Verify sorted
        for (int i = 1; i < result.Count; i++)
            result[i].CompareTo(result[i - 1]).Should().BeGreaterOrEqualTo(0,
                $"item {i} should be >= item {i - 1}");

        sorter.LastMetrics!.MergePasses.Should().BeGreaterThan(1);
    }

    [Fact]
    public void Sort_large_random_dataset()
    {
        var rng = new Random(99);
        int n = 10_000;
        var records = Enumerable.Range(0, n)
            .Select(_ => new SortRecord((ulong)rng.NextInt64(), $"word_{rng.Next(50)}"))
            .ToArray();

        // 8-way merge, ~200 items per chunk
        var sorter = CreateSorter(memoryBytes: 200 * 48, mergeWay: 8);
        using var input = WriteRecords(records);
        using var output = new MemoryStream();

        sorter.Sort(input, output);

        var result = ReadOutput(output);
        result.Should().HaveCount(n);

        for (int i = 1; i < result.Count; i++)
            result[i].CompareTo(result[i - 1]).Should().BeGreaterOrEqualTo(0);
    }

    [Fact]
    public void Sort_reports_metrics()
    {
        var records = Enumerable.Range(0, 50)
            .Select(i => new SortRecord((ulong)i, $"item_{i}"))
            .ToArray();

        var sorter = CreateSorter(memoryBytes: 48 * 10, mergeWay: 4);
        using var input = WriteRecords(records);
        using var output = new MemoryStream();

        sorter.Sort(input, output);

        var m = sorter.LastMetrics!;
        m.TotalItems.Should().Be(50);
        m.ChunksCreated.Should().BeGreaterThan(0);
        m.ChunkPhaseTime.Should().BeGreaterThan(TimeSpan.Zero);
        m.TotalTime.Should().BeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public void Sort_cancellation_throws()
    {
        var rng = new Random(42);
        var records = Enumerable.Range(0, 1000)
            .Select(_ => new SortRecord((ulong)rng.NextInt64(), "test"))
            .ToArray();

        var cts = new CancellationTokenSource();
        cts.Cancel(); // pre-cancel

        var sorter = CreateSorter(memoryBytes: 48);
        using var input = WriteRecords(records);
        using var output = new MemoryStream();

        var act = () => sorter.Sort(input, output, cts.Token);
        act.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void Sort_cleans_up_temp_files()
    {
        var records = Enumerable.Range(0, 100)
            .Select(i => new SortRecord((ulong)i, "test"))
            .ToArray();

        var sorter = CreateSorter(memoryBytes: 48);
        using var input = WriteRecords(records);
        using var output = new MemoryStream();

        sorter.Sort(input, output);

        // Temp dir should have no leftover sort_* directories
        var leftover = Directory.GetDirectories(_tempDir, "sort_*");
        leftover.Should().BeEmpty();
    }

    [Fact]
    public void Sort_1gb_with_1mb_ram()
    {
        // Core problem: sort ~1GB of data with only 1MB of RAM.
        // Uses 100K records (~1.6 MB) with 1KB memory budget to simulate
        // the same ratio (data >> RAM) without slow CI times.
        var rng = new Random(77);
        int n = 100_000;
        var records = Enumerable.Range(0, n)
            .Select(_ => new SortRecord((ulong)rng.NextInt64(), $"w{rng.Next(100)}"))
            .ToArray();

        // 1 KB memory = ~20 items per chunk → many chunks → multiple merge passes
        var sorter = CreateSorter(memoryBytes: 1024, mergeWay: 8);
        using var input = WriteRecords(records);
        using var output = new MemoryStream();

        sorter.Sort(input, output);

        var result = ReadOutput(output);
        result.Should().HaveCount(n);

        // Verify sorted
        for (int i = 1; i < result.Count; i++)
            result[i].CompareTo(result[i - 1]).Should().BeGreaterOrEqualTo(0,
                $"item {i} should be >= item {i - 1}");

        // Must have created many chunks and multiple merge passes
        var m = sorter.LastMetrics!;
        m.ChunksCreated.Should().BeGreaterThan(100,
            "with 1KB memory for 100K records, should create many chunks");
        m.MergePasses.Should().BeGreaterThan(1,
            "many chunks with 8-way merge should require multiple passes");
    }
}
