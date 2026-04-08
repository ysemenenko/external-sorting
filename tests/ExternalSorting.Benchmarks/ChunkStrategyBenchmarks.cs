using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using ExternalSorting.Core;
using ExternalSorting.Core.IO;
using ExternalSorting.Core.Pipeline;

namespace ExternalSorting.Benchmarks;

/// <summary>
/// Compares simple fixed-size chunking against Knuth's Replacement
/// Selection algorithm. RS produces runs that average 2× the heap
/// size for random input, halving the chunk count and trimming a
/// merge pass off most realistic workloads.
///
/// Both methods sort the same dataset with identical merge options;
/// the only difference is UseReplacementSelection. The benchmark
/// reports end-to-end time and (via Console.Error in [GlobalSetup])
/// the chunk-count delta so the I/O reduction is visible alongside
/// the wall-clock measurement.
/// </summary>
[MemoryDiagnoser]
[Config(typeof(StrategyConfig))]
public class ChunkStrategyBenchmarks
{
    private class StrategyConfig : ManualConfig
    {
        public StrategyConfig()
        {
            AddJob(BenchmarkDotNet.Jobs.Job.ShortRun);
            AddDiagnoser(MemoryDiagnoser.Default);
        }
    }

    [Params(50_000)]
    public int RecordCount { get; set; }

    private byte[] _inputBytes = Array.Empty<byte>();
    private string _tempDir = string.Empty;
    private readonly RecordSerializer _serializer = new();

    // Aggressively small per-chunk memory so the chunk count delta
    // between simple and RS is visible. With a giant heap one chunk
    // covers everything and there's nothing for RS to optimize.
    private const long MemBytes = 32 * 1024;

    [GlobalSetup]
    public void Setup()
    {
        var rng = new Random(2024);
        var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        for (int i = 0; i < RecordCount; i++)
        {
            _serializer.Write(bw, new SortRecord(
                (ulong)rng.NextInt64(),
                $"k{rng.Next(1000)}_{rng.Next(1000)}"));
        }
        bw.Flush();
        _inputBytes = ms.ToArray();

        _tempDir = Path.Combine(
            Path.GetTempPath(),
            $"bench_strategy_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        // One-shot run of each strategy to print the chunk count
        // delta to stderr (BDN summary table only shows time/memory).
        var (simpleChunks, simplePasses) = RunOnce(useRS: false);
        var (rsChunks, rsPasses) = RunOnce(useRS: true);
        Console.Error.WriteLine(
            $"[ChunkStrategyBenchmarks] simple: {simpleChunks} chunks, {simplePasses} merge passes; " +
            $"RS: {rsChunks} chunks, {rsPasses} merge passes " +
            $"(RS produces {1.0 - (double)rsChunks / simpleChunks:P0} fewer chunks)");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_tempDir, true); } catch { /* best effort */ }
    }

    private (int chunks, int passes) RunOnce(bool useRS)
    {
        var sorter = new ExternalSorter<SortRecord>(
            _serializer, Comparer<SortRecord>.Default, new SortOptions
            {
                MaxMemoryBytes = MemBytes,
                MergeWayCount = 8,
                TempDirectory = _tempDir,
                DegreeOfParallelism = 1,
                UseReplacementSelection = useRS,
            });
        using var input = new MemoryStream(_inputBytes, writable: false);
        using var output = new MemoryStream(capacity: _inputBytes.Length);
        sorter.Sort(input, output);
        var m = sorter.LastMetrics!;
        return (m.ChunksCreated, m.MergePasses);
    }

    [Benchmark(Baseline = true)]
    public long Sort_Simple_Chunking()
    {
        var sorter = new ExternalSorter<SortRecord>(
            _serializer, Comparer<SortRecord>.Default, new SortOptions
            {
                MaxMemoryBytes = MemBytes,
                MergeWayCount = 8,
                TempDirectory = _tempDir,
                DegreeOfParallelism = 1,
                UseReplacementSelection = false,
            });
        using var input = new MemoryStream(_inputBytes, writable: false);
        using var output = new MemoryStream(capacity: _inputBytes.Length);
        sorter.Sort(input, output);
        return output.Length;
    }

    [Benchmark]
    public long Sort_Replacement_Selection()
    {
        var sorter = new ExternalSorter<SortRecord>(
            _serializer, Comparer<SortRecord>.Default, new SortOptions
            {
                MaxMemoryBytes = MemBytes,
                MergeWayCount = 8,
                TempDirectory = _tempDir,
                UseReplacementSelection = true,
            });
        using var input = new MemoryStream(_inputBytes, writable: false);
        using var output = new MemoryStream(capacity: _inputBytes.Length);
        sorter.Sort(input, output);
        return output.Length;
    }
}
