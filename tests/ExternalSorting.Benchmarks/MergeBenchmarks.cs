using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using ExternalSorting.Core.Merge;

namespace ExternalSorting.Benchmarks;

/// <summary>
/// Isolates the k-way merge inner loop from disk I/O so the speedup
/// from MinHeap.ReplaceMin (vs ExtractMin + Insert) is measurable
/// without being drowned out by FileStream / GC noise.
///
/// Two methods receive identical pre-sorted in-memory inputs and run
/// the same merge logic with two different heap operation patterns:
///
///   Merge_ExtractMin_Insert  — old code path (Pop, then Push)
///                              two SiftDown + SiftUp operations per item
///   Merge_ReplaceMin         — new code path (Peek + ReplaceMin)
///                              one SiftDown per item, fast path when
///                              the source still has more data
///
/// Both produce the same XOR fold of the merged stream so the JIT
/// can't dead-code-eliminate the merge work.
/// </summary>
[MemoryDiagnoser]
[Config(typeof(MergeConfig))]
public class MergeBenchmarks
{
    private class MergeConfig : ManualConfig
    {
        public MergeConfig()
        {
            AddJob(BenchmarkDotNet.Jobs.Job.ShortRun);
            AddDiagnoser(MemoryDiagnoser.Default);
        }
    }

    [Params(8, 16)]
    public int K { get; set; }

    [Params(1_000_000)]
    public int TotalItems { get; set; }

    private List<long>[] _sources = null!;

    [GlobalSetup]
    public void Setup()
    {
        // K pre-sorted random sources of equal size. Doing the sort
        // in setup keeps the merge benchmark itself from being
        // contaminated by Array.Sort time.
        var rng = new Random(42);
        _sources = new List<long>[K];
        int per = TotalItems / K;
        for (int k = 0; k < K; k++)
        {
            var src = new List<long>(per);
            for (int i = 0; i < per; i++)
                src.Add(rng.NextInt64());
            src.Sort();
            _sources[k] = src;
        }
    }

    [Benchmark(Baseline = true)]
    public long Merge_ExtractMin_Insert()
    {
        var heap = new MinHeap<(long Value, int SrcIdx)>(
            Comparer<(long Value, int SrcIdx)>.Create(
                (a, b) => a.Value.CompareTo(b.Value)),
            K);
        var indices = new int[K];

        // Seed heap with first item from each source
        for (int k = 0; k < K; k++)
        {
            heap.Insert((_sources[k][0], k));
            indices[k] = 1;
        }

        long fold = 0;
        while (heap.Count > 0)
        {
            var (val, idx) = heap.ExtractMin();
            fold ^= val;  // prevents JIT dead-code elimination
            if (indices[idx] < _sources[idx].Count)
            {
                heap.Insert((_sources[idx][indices[idx]], idx));
                indices[idx]++;
            }
        }
        return fold;
    }

    [Benchmark]
    public long Merge_ReplaceMin()
    {
        var heap = new MinHeap<(long Value, int SrcIdx)>(
            Comparer<(long Value, int SrcIdx)>.Create(
                (a, b) => a.Value.CompareTo(b.Value)),
            K);
        var indices = new int[K];

        for (int k = 0; k < K; k++)
        {
            heap.Insert((_sources[k][0], k));
            indices[k] = 1;
        }

        long fold = 0;
        while (heap.Count > 0)
        {
            var (val, idx) = heap.Peek();
            fold ^= val;
            if (indices[idx] < _sources[idx].Count)
            {
                heap.ReplaceMin((_sources[idx][indices[idx]], idx));
                indices[idx]++;
            }
            else
            {
                heap.ExtractMin();
            }
        }
        return fold;
    }
}
