using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Order;
using ExternalSorting.Core;
using ExternalSorting.Core.IO;
using ExternalSorting.Core.Pipeline;

namespace ExternalSorting.Benchmarks;

/// <summary>
/// Full end-to-end sort benchmark — chunk phase + merge phase combined.
/// Sweeps DegreeOfParallelism so the parallel chunk creation win is
/// visible against the serial baseline (DegreeOfParallelism=1).
///
/// The dataset is held in a MemoryStream so disk speed doesn't dominate.
/// Real disk numbers will be slower but the speedup ratios stay similar.
/// </summary>
[MemoryDiagnoser]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[Config(typeof(QuickConfig))]
public class SortBenchmarks
{
    private class QuickConfig : ManualConfig
    {
        public QuickConfig()
        {
            // BDN defaults run too long for a tutorial benchmark — use
            // ShortRun (3 warmup + 5 measure iterations) so the whole
            // suite finishes in ~30s instead of ~5min.
            AddJob(BenchmarkDotNet.Jobs.Job.ShortRun);
            AddDiagnoser(MemoryDiagnoser.Default);
        }
    }

    [Params(50_000)]
    public int RecordCount { get; set; }

    [Params(1, 2, 4, 8)]
    public int Parallelism { get; set; }

    private byte[] _inputBytes = Array.Empty<byte>();
    private string _tempDir = string.Empty;
    private readonly RecordSerializer _serializer = new();

    [GlobalSetup]
    public void Setup()
    {
        // Deterministic random dataset
        var rng = new Random(42);
        var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        for (int i = 0; i < RecordCount; i++)
        {
            _serializer.Write(bw, new SortRecord(
                (ulong)rng.NextInt64(),
                $"w{rng.Next(1000)}_{rng.Next(1000)}"));
        }
        bw.Flush();
        _inputBytes = ms.ToArray();

        _tempDir = Path.Combine(
            Path.GetTempPath(),
            $"bench_sort_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_tempDir, true); } catch { /* best effort */ }
    }

    [Benchmark]
    public long Sort_End_To_End()
    {
        // Aggressively small per-chunk memory so the chunk phase is the
        // hot path (40-80 chunks for 50K records). This is what makes
        // the parallelism comparison meaningful — with one giant chunk
        // there's nothing to parallelize.
        var sorter = new ExternalSorter<SortRecord>(
            _serializer, Comparer<SortRecord>.Default, new SortOptions
            {
                MaxMemoryBytes = 32 * 1024,
                MergeWayCount = 8,
                TempDirectory = _tempDir,
                DegreeOfParallelism = Parallelism,
            });

        using var input = new MemoryStream(_inputBytes, writable: false);
        using var output = new MemoryStream(capacity: _inputBytes.Length);
        sorter.Sort(input, output);
        return output.Length;
    }
}
