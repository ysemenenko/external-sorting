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

    // ── Replacement Selection ───────────────────────────────────────────

    private ExternalSorter<SortRecord> CreateSorterRS(long memoryBytes = 4096, int mergeWay = 8)
    {
        return new ExternalSorter<SortRecord>(_serializer, Comparer<SortRecord>.Default, new SortOptions
        {
            MaxMemoryBytes = memoryBytes,
            MergeWayCount = mergeWay,
            TempDirectory = _tempDir,
            UseReplacementSelection = true,
            DegreeOfParallelism = 1,  // RS ignores parallelism anyway
        });
    }

    [Fact]
    public void Sort_RS_empty_input()
    {
        var sorter = CreateSorterRS();
        using var input = new MemoryStream();
        using var output = new MemoryStream();

        sorter.Sort(input, output);

        output.Length.Should().Be(0);
        sorter.LastMetrics!.ChunksCreated.Should().Be(0);
    }

    [Fact]
    public void Sort_RS_single_item()
    {
        var sorter = CreateSorterRS();
        using var input = WriteRecords(new SortRecord(7, "Apple"));
        using var output = new MemoryStream();

        sorter.Sort(input, output);

        var result = ReadOutput(output);
        result.Should().Equal(new SortRecord(7, "Apple"));
        sorter.LastMetrics!.ChunksCreated.Should().Be(1);
    }

    [Fact]
    public void Sort_RS_already_sorted_collapses_to_single_run()
    {
        // Best case for RS: ascending input → entire stream becomes one run
        // because every new item is ≥ the just-emitted one.
        var records = Enumerable.Range(0, 200)
            .Select(i => new SortRecord((ulong)i, $"item_{i:D4}"))
            .ToArray();

        // Heap of ~10 items would normally produce ~20 chunks of 10 each.
        // RS with ascending input should produce exactly 1 chunk.
        var sorter = CreateSorterRS(memoryBytes: 48 * 10, mergeWay: 4);
        using var input = WriteRecords(records);
        using var output = new MemoryStream();

        sorter.Sort(input, output);

        var result = ReadOutput(output);
        result.Should().Equal(records);
        sorter.LastMetrics!.ChunksCreated.Should().Be(1,
            "ascending input → RS produces a single run");
    }

    [Fact]
    public void Sort_RS_reverse_order_degenerates_to_M_sized_runs()
    {
        // Worst case for RS: descending input → every new item goes to
        // the next run, so chunks are exactly heap-sized (no improvement
        // over simple chunking, but no regression either).
        var records = Enumerable.Range(0, 30)
            .Select(i => new SortRecord((ulong)(100 - i), $"x{100 - i:D3}"))
            .ToArray();

        var sorter = CreateSorterRS(memoryBytes: 48 * 5);  // ~5 items per chunk
        using var input = WriteRecords(records);
        using var output = new MemoryStream();

        sorter.Sort(input, output);

        var result = ReadOutput(output);
        result.Should().HaveCount(30);
        result.Should().BeInAscendingOrder(r => r);
    }

    [Fact]
    public void Sort_RS_random_input_produces_fewer_chunks_than_simple()
    {
        // The headline win: random input → ~2x larger runs → ~half as
        // many chunks vs the simple fixed-size chunking path.
        var rng = new Random(2024);
        int n = 1000;
        var records = Enumerable.Range(0, n)
            .Select(_ => new SortRecord((ulong)rng.NextInt64(0, 1_000_000), $"k{rng.Next(100)}"))
            .ToArray();

        // Identical sort options except for the algorithm switch
        const long memBytes = 48 * 50;  // ~50 items per chunk

        var simple = CreateSorter(memoryBytes: memBytes);
        using (var input = WriteRecords(records))
        using (var output = new MemoryStream())
        {
            simple.Sort(input, output);
        }

        var rs = CreateSorterRS(memoryBytes: memBytes);
        using (var input = WriteRecords(records))
        using (var output = new MemoryStream())
        {
            rs.Sort(input, output);
        }

        int simpleChunks = simple.LastMetrics!.ChunksCreated;
        int rsChunks = rs.LastMetrics!.ChunksCreated;

        // For random input on a heap of size M, average run is ~2M.
        // Allow some slack — Knuth's 2x is a probabilistic average,
        // not a worst-case bound. Anything below 80% of the simple
        // chunk count is a clear win.
        rsChunks.Should().BeLessThan((int)(simpleChunks * 0.8),
            $"RS should produce noticeably fewer chunks than simple chunking " +
            $"(simple={simpleChunks}, rs={rsChunks})");
    }

    [Fact]
    public void Sort_RS_correctness_random_dataset()
    {
        var rng = new Random(13);
        int n = 5000;
        var records = Enumerable.Range(0, n)
            .Select(_ => new SortRecord((ulong)rng.NextInt64(), $"w{rng.Next(50)}"))
            .ToArray();

        var sorter = CreateSorterRS(memoryBytes: 48 * 30, mergeWay: 4);
        using var input = WriteRecords(records);
        using var output = new MemoryStream();

        sorter.Sort(input, output);

        var result = ReadOutput(output);
        result.Should().HaveCount(n);
        for (int i = 1; i < result.Count; i++)
            result[i].CompareTo(result[i - 1]).Should().BeGreaterOrEqualTo(0);
    }

    [Fact]
    public void Sort_RS_cancellation_throws()
    {
        var rng = new Random(42);
        var records = Enumerable.Range(0, 1000)
            .Select(_ => new SortRecord((ulong)rng.NextInt64(), "test"))
            .ToArray();

        var cts = new CancellationTokenSource();
        cts.Cancel();

        var sorter = CreateSorterRS(memoryBytes: 48);
        using var input = WriteRecords(records);
        using var output = new MemoryStream();

        var act = () => sorter.Sort(input, output, cts.Token);
        act.Should().Throw<OperationCanceledException>();
    }

    // ── Parallel chunk creation correctness ─────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void Sort_parallel_matches_serial_on_random_input(int parallelism)
    {
        // Same dataset, same merge options — only DegreeOfParallelism
        // varies. Output bytes must be identical to the serial baseline,
        // proving the parallel pipeline doesn't reorder, drop, duplicate,
        // or mangle anything.
        var rng = new Random(31337);
        int n = 2000;
        var records = Enumerable.Range(0, n)
            .Select(_ => new SortRecord(
                (ulong)rng.NextInt64(),
                $"k{rng.Next(200)}_{rng.Next(200)}"))
            .ToArray();

        byte[] Run(int p)
        {
            var sorter = new ExternalSorter<SortRecord>(
                _serializer, Comparer<SortRecord>.Default, new SortOptions
                {
                    MaxMemoryBytes = 48 * 20,  // ~20 items per chunk
                    MergeWayCount = 4,
                    TempDirectory = _tempDir,
                    DegreeOfParallelism = p,
                });
            using var input = WriteRecords(records);
            using var output = new MemoryStream();
            sorter.Sort(input, output);
            return output.ToArray();
        }

        byte[] baseline = Run(1);
        byte[] underTest = Run(parallelism);

        underTest.Should().Equal(baseline,
            $"DegreeOfParallelism={parallelism} must produce byte-identical " +
            $"output to the serial baseline");
    }

    [Fact]
    public void Sort_parallel_stress_repeated_runs_are_deterministic()
    {
        // Run the same parallel sort 25 times. Any race condition that
        // would corrupt output (lost item, double-write, wrong chunk
        // index assignment) tends to surface non-deterministically;
        // doing many repeats catches it. Output bytes must match across
        // every iteration.
        var rng = new Random(7);
        int n = 1500;
        var records = Enumerable.Range(0, n)
            .Select(_ => new SortRecord((ulong)rng.NextInt64(), $"r{rng.Next(50)}"))
            .ToArray();

        byte[]? expected = null;
        for (int i = 0; i < 25; i++)
        {
            var sorter = new ExternalSorter<SortRecord>(
                _serializer, Comparer<SortRecord>.Default, new SortOptions
                {
                    MaxMemoryBytes = 48 * 15,
                    MergeWayCount = 4,
                    TempDirectory = _tempDir,
                    DegreeOfParallelism = 8,  // max contention
                });
            using var input = WriteRecords(records);
            using var output = new MemoryStream();
            sorter.Sort(input, output);

            byte[] bytes = output.ToArray();
            if (expected is null)
            {
                expected = bytes;
                continue;
            }
            bytes.Should().Equal(expected,
                $"iteration {i}: parallel sort must be deterministic");
        }
    }

    [Fact]
    public void Sort_parallel_chunk_count_matches_serial()
    {
        // ChunksCreated metric should be the same regardless of
        // parallelism — same input, same chunk capacity, same number
        // of full buffers. Catches off-by-one errors in chunk index
        // assignment under concurrent worker reads.
        var rng = new Random(101);
        var records = Enumerable.Range(0, 500)
            .Select(_ => new SortRecord((ulong)rng.NextInt64(), $"x{rng.Next(20)}"))
            .ToArray();

        int CountChunks(int p)
        {
            var sorter = new ExternalSorter<SortRecord>(
                _serializer, Comparer<SortRecord>.Default, new SortOptions
                {
                    MaxMemoryBytes = 48 * 25,
                    MergeWayCount = 4,
                    TempDirectory = _tempDir,
                    DegreeOfParallelism = p,
                });
            using var input = WriteRecords(records);
            using var output = new MemoryStream();
            sorter.Sort(input, output);
            return sorter.LastMetrics!.ChunksCreated;
        }

        int serialChunks = CountChunks(1);
        CountChunks(2).Should().Be(serialChunks);
        CountChunks(4).Should().Be(serialChunks);
        CountChunks(8).Should().Be(serialChunks);
    }

    [Fact]
    public void Sort_parallel_cancellation_unblocks_pipeline()
    {
        // Pre-cancelled token: the parallel pipeline must tear down
        // without deadlocking the bounded queue. Reader detects CT,
        // CompleteAdding fires from the finally block, workers see
        // queue done and exit, WaitAll returns or surfaces OCE.
        var rng = new Random(42);
        var records = Enumerable.Range(0, 5000)
            .Select(_ => new SortRecord((ulong)rng.NextInt64(), "t"))
            .ToArray();

        var cts = new CancellationTokenSource();
        cts.Cancel();

        var sorter = new ExternalSorter<SortRecord>(
            _serializer, Comparer<SortRecord>.Default, new SortOptions
            {
                MaxMemoryBytes = 48,  // tiny → many small chunks → max queue churn
                MergeWayCount = 8,
                TempDirectory = _tempDir,
                DegreeOfParallelism = 8,
            });
        using var input = WriteRecords(records);
        using var output = new MemoryStream();

        // Should throw OperationCanceledException, not hang
        var act = () => sorter.Sort(input, output, cts.Token);
        act.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void Sort_replacement_selection_overrides_parallelism()
    {
        // RS is mutually exclusive with parallel chunk creation
        // (single shared heap). When both options are set, RS wins
        // and the resulting chunk file names start with "chunk_rs_"
        // (the RS code path's prefix). The simple path uses
        // "chunk_". This is an indirect way to assert RS was chosen
        // — we read the metrics ChunksCreated from the RS path which
        // also produces fewer chunks for random input.
        var rng = new Random(1234);
        var records = Enumerable.Range(0, 1000)
            .Select(_ => new SortRecord((ulong)rng.NextInt64(0, 100_000), $"k{rng.Next(50)}"))
            .ToArray();

        const long memBytes = 48 * 30;

        // Both options set — RS should take priority
        var rsAndParallel = new ExternalSorter<SortRecord>(
            _serializer, Comparer<SortRecord>.Default, new SortOptions
            {
                MaxMemoryBytes = memBytes,
                MergeWayCount = 4,
                TempDirectory = _tempDir,
                UseReplacementSelection = true,
                DegreeOfParallelism = 8,
            });
        using (var input = WriteRecords(records))
        using (var output = new MemoryStream())
        {
            rsAndParallel.Sort(input, output);
        }

        // Pure parallel for comparison
        var parallelOnly = new ExternalSorter<SortRecord>(
            _serializer, Comparer<SortRecord>.Default, new SortOptions
            {
                MaxMemoryBytes = memBytes,
                MergeWayCount = 4,
                TempDirectory = _tempDir,
                UseReplacementSelection = false,
                DegreeOfParallelism = 8,
            });
        using (var input = WriteRecords(records))
        using (var output = new MemoryStream())
        {
            parallelOnly.Sort(input, output);
        }

        // RS produces noticeably fewer chunks on random input
        rsAndParallel.LastMetrics!.ChunksCreated
            .Should().BeLessThan((int)(parallelOnly.LastMetrics!.ChunksCreated * 0.8),
                "RS path should be chosen over parallel even when both options are set");
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
