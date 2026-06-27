# [External Sorting in C# / .NET 8](https://github.com/ysemenenko/external-sorting)

[![NuGet](https://img.shields.io/nuget/v/ExternalSorting.Core.svg)](https://www.nuget.org/packages/ExternalSorting.Core)

Sort 1 GB of data with 1 MB of RAM. K-way external merge sort implementation using binary min-heap.

[Source code on GitHub](https://github.com/ysemenenko/external-sorting)

Handles datasets larger than available memory by splitting input into sorted chunks on disk and merging them with O(N log K) k-way merge. Generic `IExternalSorter<T>` — plug in any record type, comparer, and serializer.

**Keywords**: external sort, external merge sort, k-way merge, out-of-core sorting, large file sorting, limited memory sorting, disk-based sort, C#, .NET 8, binary min-heap

## Architecture

```
ExternalSorting.Core/
├── Core/
│   ├── IExternalSorter<T>    — main contract: Stream → sorted Stream
│   ├── ISerializer<T>        — binary serialization for any record type
│   ├── SortOptions            — memory, merge, parallelism, replacement-selection
│   └── SortMetrics            — items, chunks, merge passes, timing
├── Pipeline/
│   └── ExternalSorter<T>     — orchestrator with three chunk strategies:
│                                  • serial (1 thread, 1 recycled buffer)
│                                  • parallel (N workers, bounded queue)
│                                  • replacement selection (~2x larger runs)
├── Merge/
│   └── MinHeap<T>            — O(log K) binary min-heap with ReplaceMin
└── IO/
    ├── ChunkWriter/Reader     — buffered binary chunk I/O with headers
    ├── RecordSerializer       — SortRecord (ulong + string) binary format
    ├── TextRecordIO           — legacy "number. text" format parser
    └── DataGenerator          — random test data generation
```

### Algorithm: K-Way External Merge Sort

External merge sort handles datasets that don't fit in RAM by splitting the work into two phases: **chunk creation** (fits in memory) and **multi-pass merging** (disk-based).

#### Phase 1 — Chunk Creation

```
Input stream (N items, unsorted)
        │
        ▼
┌─────────────────────────────┐
│  Read M items into memory   │  M = MaxMemoryBytes / EstimatedItemSize
│  Sort in-memory (Array.Sort)│  O(M log M) per chunk
│  Write sorted chunk to disk │  Binary format with item count header
└─────────────────────────────┘
        │ repeat until input exhausted
        ▼
Chunk₀  Chunk₁  Chunk₂  ...  Chunk_{C-1}     (C = ⌈N/M⌉ chunks)
```

Each chunk is a self-contained binary file: `[int32: count][item₀][item₁]...[item_{M-1}]`.

#### Phase 2 — K-Way Merge

Merge K sorted chunks at a time using a **binary min-heap** of size K:

```
Pass 0: C chunks → ⌈C/K⌉ merged chunks
Pass 1: ⌈C/K⌉ chunks → ⌈C/K²⌉ merged chunks
...
Pass P: 1 final sorted output

Total passes: P = ⌈log_K(C)⌉
```

Each merge step:

```
Chunk A:  [1, 5, 9, ...]     ──┐
Chunk B:  [2, 3, 8, ...]     ──┤
Chunk C:  [4, 6, 7, ...]     ──┼──→  MinHeap (size K=3)  ──→  Output: [1, 2, 3, 4, 5, 6, ...]
                                │
                                │     ExtractMin: O(log K)
                                │     Insert replacement from same chunk: O(log K)
                                │     Total: O(N log K) per pass
```

**Why MinHeap?** The old implementation used `List.Sort()` on every extraction — O(K log K) per item, O(NK log K) total. MinHeap gives O(N log K), which is orders of magnitude faster for large K (8-way, 16-way merge).

**ReplaceMin fast path.** When the source that just yielded the min still has more data, the merge loop overwrites the heap root in place via `MinHeap.ReplaceMin` (one SiftDown) instead of doing `ExtractMin + Insert` (SiftDown + SiftUp). Measured **26% speedup at both K=8 and K=16** on the merge inner loop — see [Benchmarks](#benchmarks).

#### Phase 1 alternatives — Replacement Selection

The simple chunking above produces runs that are exactly *M* items long. **Knuth's Replacement Selection** (TAOCP Vol. 3, §5.4.1) does better: by keeping the heap "live" across the entire input stream and routing items to a "next run" when they would break sorted order, it produces runs that average **2 × M** for random input.

```
heap = M items from input, all tagged "run 0"
current_run = 0
while heap not empty:
    (run, item) = heap.extractMin()
    if run != current_run:
        close current chunk file, open a new one for the new run
        current_run = run
    write item to current chunk
    next = read one item from input
    if next >= just-emitted item:
        heap.insert((current_run, next))      # extends current run
    else:
        heap.insert((current_run + 1, next))  # frozen for next run
```

Result on a 50K random dataset with 32 KB heap: **74 chunks → 38 chunks** (49% fewer), **3 merge passes → 2** (one fewer disk pass), **32% less memory allocated**. Best case (already-sorted input) collapses the entire stream into a single run; worst case (reverse-sorted) degenerates to *M*-sized runs with no improvement and no regression.

Opt in via `SortOptions.UseReplacementSelection = true`. Inherently single-threaded so it ignores `DegreeOfParallelism`.

#### Phase 1 alternatives — Pipelined parallel chunking

When `SortOptions.DegreeOfParallelism > 1`, chunk creation runs as a producer/consumer pipeline:

```
Reader (1 thread) ──► [bounded queue, capacity = parallelism × 2] ──► Workers (N threads)
       │                                                                    │
   read input into                                                      buffer.Sort() +
   per-chunk buffer                                                     ChunkWriter.Write
```

The bounded `BlockingCollection` caps in-flight buffers so memory growth is bounded by `~(parallelism + 1) × MaxMemoryBytes`. A linked `CancellationTokenSource` propagates worker faults back to the reader so a disk-full error tears the pipeline down cleanly instead of deadlocking. The multi-pass merge is also parallel — the independent per-pass batches merge concurrently — so chunking and merging both scale; combined they reach **~1.54× end-to-end** at the P=4–8 sweet spot for in-memory workloads (see [Benchmarks](#benchmarks)), wider for disk-bound ones because writes overlap with sorting.

#### Concrete Example

Sort 10M records with 64 MB memory, 8-way merge:

```
Input: 10,000,000 records (158 MB on disk)

Phase 1 — Chunk Creation:
  M = 64 MB / 48 bytes ≈ 1,300,000 items per chunk
  C = ⌈10M / 1.3M⌉ = 8 chunks
  Each chunk: ~20 MB, internally sorted

  Chunk₀: [Apple:1, Apple:5, Banana:2, ...]     (1.3M items, sorted)
  Chunk₁: [Apple:3, Cherry:8, Date:1, ...]      (1.3M items, sorted)
  ...
  Chunk₇: [Mango:4, Zucchini:9, ...]            (remaining items, sorted)

Phase 2 — 8-Way Merge:
  Pass 0: merge all 8 chunks in one pass (K=8 ≥ C=8)
  
  MinHeap seeded with first item from each chunk:
  Heap: [(Apple:1, chunk0), (Apple:3, chunk1), ..., (Mango:4, chunk7)]
  
  Loop 10M times:
    1. ExtractMin → smallest item across all chunks    O(log 8) = O(3)
    2. Write to output
    3. Read next item from same chunk, insert to heap  O(log 8) = O(3)
  
  Total comparisons: 10M × 2 × log₂(8) = 60M comparisons

Result: single sorted file, 10M items in order
Time: 9.8s (6.1s chunking + 3.3s merging)
```

#### Complexity

| Metric | Formula | 10M example |
|--------|---------|-------------|
| Chunk count | C = ⌈N/M⌉ | 8 |
| Merge passes | P = ⌈log_K(C)⌉ | 1 |
| Comparisons per pass | O(N log K) | ~60M |
| Total comparisons | O(N log K × P) | ~60M |
| Disk I/O passes | P + 1 (chunk + merge) | 2 |
| Total bytes read/written | O(N × (P + 1)) | ~316 MB × 2 |

**Key insight**: Increasing K reduces passes (fewer disk I/O rounds) but increases heap operations per item. K=8 to K=16 is the sweet spot for most workloads — one merge pass handles up to K^1 = 8-16 chunks, and two passes handle up to K^2 = 64-256 chunks (billions of records).

## Installation

```bash
dotnet add package ExternalSorting.Core --version 1.0.4
```

## Quick Start

```bash
# Build
dotnet build

# Run tests (51 tests)
dotnet test

# Sort 100K records (quick check)
dotnet run --project src/ExternalSorting.Console -- -n 100000 -m 8

# Sort 1M records (release mode, faster)
dotnet run --project src/ExternalSorting.Console -c Release -- -n 1000000 -m 16 -k 4

# Sort 10M records (stress test)
dotnet run --project src/ExternalSorting.Console -c Release -- -n 10000000 -m 64 -k 8
```

## Usage

```bash
dotnet run --project src/ExternalSorting.Console -- [options]

Options:
  -n, --count <N>      Number of records to generate (default: 1M)
  -m, --memory <MB>    Memory budget in MB (default: 64)
  -k, --merge-way <K>  K-way merge factor (default: 8)
  -i, --input <file>   Use existing binary input file (skip generation)
  -h, --help           Show help
```

### Examples

```bash
# Sort 1M records with 16MB memory, 8-way merge
dotnet run --project src/ExternalSorting.Console -c Release -- -n 1000000 -m 16 -k 8

# Sort 10M records with 64MB memory
dotnet run --project src/ExternalSorting.Console -c Release -- -n 10000000 -m 64
```

### Programmatic API

```csharp
var serializer = new RecordSerializer();
var comparer = Comparer<SortRecord>.Default;
var options = new SortOptions
{
    MaxMemoryBytes = 64 * 1024 * 1024,  // 64 MB
    MergeWayCount = 8,
    BufferSize = 64 * 1024,             // FileStream buffer

    // Phase 3.1 — pipelined parallel chunk creation. One reader thread
    // feeds N sort+write workers via a bounded queue. Default = ProcessorCount.
    DegreeOfParallelism = Environment.ProcessorCount,

    // Phase 3.2 — Replacement Selection. Produces ~2x larger runs on
    // random input → fewer chunks → fewer merge passes. Mutually
    // exclusive with parallel chunking (single-heap algorithm).
    UseReplacementSelection = false,

    OnProgress = (phase, pct) => Console.Write($"\r{phase} {pct:F0}%"),
};

var sorter = new ExternalSorter<SortRecord>(serializer, comparer, options);

using var input = File.OpenRead("input.bin");
using var output = File.Create("output.bin");
sorter.Sort(input, output);

Console.WriteLine(sorter.LastMetrics); // Items: 1,000,000, Chunks: 3, ...
```

#### Picking a chunk strategy

| Workload | Recommended | Why |
|---|---|---|
| Default / unknown | parallel (default) | linear-ish speedup with cores, no algorithm risk |
| Memory-constrained, random input | `UseReplacementSelection = true` | ~50% fewer chunks → one fewer merge pass → less disk I/O |
| Mostly-sorted input | `UseReplacementSelection = true` | best case collapses entire stream into a single run |
| Reverse-sorted input | parallel | RS degenerates to *M*-sized runs, parallel still wins |
| Single-core or strict memory cap | `DegreeOfParallelism = 1` | original serial path, single recycled buffer, lowest GC |

### Custom record types

Implement `ISerializer<T>` for any type:

```csharp
public record LogEntry(DateTime Timestamp, string Message);

public class LogSerializer : ISerializer<LogEntry>
{
    public int EstimatedItemSize => 8 + 100;
    public void Write(BinaryWriter w, LogEntry item) { ... }
    public LogEntry Read(BinaryReader r) { ... }
}
```

## Performance

| Records | Data Size | Memory | Merge | Chunks | Passes | Time | Verified |
|---------|-----------|--------|-------|--------|--------|------|----------|
| 100K | 1.6 MB | 8 MB | 4-way | 1 | 0 | 0.1s | OK |
| 1M | 16 MB | 16 MB | 8-way | 3 | 1 | 1.3s | OK |
| 10M | 158 MB | 64 MB | 8-way | 8 | 1 | 9.8s | OK |
| **60M** | **948 MB** | **1 MB** | **8-way** | **2,747** | **4** | **84s** | **OK** |

The last row demonstrates the core interview problem: **sort 1 GB of data with only 1 MB of RAM** — a classic system design / algorithms challenge.

## Tests

51 tests covering:
- **MinHeap**: insert, extract, duplicates, replace, 10K random
- **Serializer**: binary roundtrip, text parse/format, comparison logic
- **Chunk I/O**: write/read roundtrip, empty, dispose cleanup, 10K items
- **ExternalSorter**:
  - basics: empty, single, sorted, reverse, duplicates, multi-chunk,
    multi-pass, 10K random, cancellation, temp cleanup, metrics
  - **Replacement Selection**: empty, single, ascending → 1 chunk
    (best case), descending → *M*-sized runs (worst case), random
    input produces noticeably fewer chunks than simple chunking,
    correctness on 5K random, cancellation
  - **Parallel chunk creation**: byte-identical output across
    `DegreeOfParallelism` ∈ {1,2,4,8} (Theory), 25-iteration
    determinism stress (max contention), chunk count invariant
    across parallelism, pre-cancelled CT unblocks pipeline cleanly,
    RS overrides parallelism when both options are set
- **DataGenerator**: binary/text generation, deterministic seed

```bash
dotnet test
```

## Benchmarks

A separate `tests/ExternalSorting.Benchmarks/` project measures the
perf-relevant inner loops with [BenchmarkDotNet](https://benchmarkdotnet.org/).

```bash
# Run all benchmark suites (~1-2 min total, ShortRun config)
dotnet run -c Release --project tests/ExternalSorting.Benchmarks -- --filter '*'

# Or pick one
dotnet run -c Release --project tests/ExternalSorting.Benchmarks -- --filter '*MergeBenchmarks*'
```

All numbers below were measured on an **AMD Ryzen 7 9800X3D (8 physical /
16 logical cores), .NET 8.0.28**, ShortRun config (7 warmup + 20 measured
iterations, margin <1.1% of mean).

Three suites:

### MergeBenchmarks — `MinHeap.ReplaceMin` vs `ExtractMin + Insert`

Isolates the merge inner loop from disk I/O. K pre-sorted in-memory
sources (1M items merged), two methods running the same merge with
different heap operation patterns.

| Method | K | Mean | Ratio |
|---|---|---|---|
| Merge_ExtractMin_Insert | 8 | 23.91 ms | 1.00 |
| **Merge_ReplaceMin** | **8** | **17.80 ms** | **0.74** |
| Merge_ExtractMin_Insert | 16 | 30.55 ms | 1.00 |
| **Merge_ReplaceMin** | **16** | **22.71 ms** | **0.74** |

`ReplaceMin` is **~26% faster** on the inner merge loop at both K. (On a
smaller-cache Intel i3-10100T the same code measured 30–34% — the X3D's
large L3 hides more of the memory latency that SiftUp would otherwise
cost, so the relative win narrows while absolute times roughly halve.)

### ChunkStrategyBenchmarks — Replacement Selection vs simple chunking

Same dataset (50K random records, 32 KB heap), only the chunk
algorithm differs.

| Method | Mean | Allocated | Chunks | Merge passes |
|---|---|---|---|---|
| Sort_Simple_Chunking | 23.37 ms | 22.96 MB | 74 | 3 |
| **Sort_Replacement_Selection** | 24.75 ms | **15.63 MB** | **38** | **2** |

RS halves the chunk count and saves a full merge pass on disk. On this
in-memory benchmark RS is ~6% slower in wall-clock because the heap
operations during chunking cost more than `Array.Sort` and the saved
merge pass never touches a real disk — but **memory allocation drops
32%** and on real disk-bound workloads the one fewer merge pass
dominates. (Chunk count and merge passes are algorithm-deterministic, so
they're identical to any other machine.)

### SortBenchmarks — `DegreeOfParallelism` sweep

End-to-end sort of a 50K-record in-memory dataset with tiny per-chunk
memory (~74 chunks, 3 merge passes) so the parallelism comparison is
meaningful. **Both columns were measured on the same Ryzen 9800X3D with the
same config — only the merge implementation differs**, so the delta isolates
the code change, not a hardware change.

| Parallelism | Serial merge | Parallel merge | Speedup (parallel) |
|---|---|---|---|
| 1 (serial) | 23.72 ms | 23.70 ms | 1.00× |
| 2 | 21.12 ms | 17.93 ms | 1.32× |
| 4 | 19.82 ms | 15.75 ms | 1.50× |
| 8 ⭐ | 20.20 ms | **15.41 ms** | **1.54×** |
| 16 | 20.90 ms | 15.73 ms | 1.51× |

Originally only chunk creation ran in parallel; the multi-pass merge was
fully single-threaded, so **Amdahl's law capped the end-to-end speedup at
~1.20×** no matter how many cores were free (the "Serial merge" column).
Merging the independent per-pass batches concurrently lifted that to
**~1.54×** and moved the sweet spot from P=4 to the P=4–8 plateau —
roughly **−24% wall-clock at P=8**. The remaining serial tail is the last
single-batch pass plus the final output write; P=16 dips slightly because
the 16 SMT threads are oversubscribed. Allocation stays ~23.75 MB across the
sweep, which is *why* it was never the scaling ceiling — the ceiling was the
serial merge, a software limit, not memory bandwidth or core count. On real
disk I/O the gap widens further because parallel writes overlap sorting.

## Project Structure

```
external-sorting/
├── ExternalSorting.sln
├── src/
│   ├── ExternalSorting.Core/         — library (algorithm + I/O)
│   └── ExternalSorting.Console/      — CLI application
└── tests/
    ├── ExternalSorting.Tests/        — xUnit + FluentAssertions (51 tests)
    └── ExternalSorting.Benchmarks/   — BenchmarkDotNet perf suites
```

## Key Design Decisions

- **Generic `T`**: Sort any type, not just strings — plug in your own `ISerializer<T>` and `IComparer<T>`
- **MinHeap merge**: O(N log K) vs old code's O(NK log K) — orders of magnitude faster for large K
- **`ReplaceMin` fast path**: merge inner loop overwrites the heap root in place when the source still has data, saving the SiftUp half of an `ExtractMin + Insert` pair (~26% measured speedup, see [Benchmarks](#benchmarks))
- **Three chunk strategies**: serial (lowest GC), parallel pipeline (default), Replacement Selection (~50% fewer chunks for random input). Dispatcher picks one in `SortOptions`. With the parallel merge this scales to **~1.54× end-to-end**.
- **Binary format**: 3-5x faster I/O than text parsing
- **Memory-adaptive chunking**: Chunk size computed from `MaxMemoryBytes / EstimatedItemSize`
- **Automatic cleanup**: Temp directory deleted in `finally` block, `ChunkFile` implements `IDisposable`
- **CancellationToken**: Cooperative cancellation at chunk and merge boundaries; parallel pipeline uses a linked CTS so a worker fault unblocks the reader instead of deadlocking
- **Progress reporting**: Callback with phase + percentage for UI integration

## Requirements

- .NET 8.0 SDK

```bash
# Ubuntu/Debian
sudo apt-get install -y dotnet-sdk-8.0

# macOS
brew install dotnet-sdk

# Verify
dotnet --version
```

## References

- [External sorting — Wikipedia](https://en.wikipedia.org/wiki/External_sorting)
- [K-way merge algorithm — Wikipedia](https://en.wikipedia.org/wiki/K-way_merge_algorithm)
- [Binary heap — Wikipedia](https://en.wikipedia.org/wiki/Binary_heap)
- [Knuth, TAOCP Vol. 3 — Sorting and Searching, Ch. 5.4: External Sorting](https://en.wikipedia.org/wiki/The_Art_of_Computer_Programming)
- [Sort 1GB with 1MB RAM — classic system design problem](https://en.wikipedia.org/wiki/External_sorting#External_merge_sort)
- [.NET 8 documentation](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-8)

## Related Topics

If you're studying this problem, you might also be interested in:

- [MapReduce](https://en.wikipedia.org/wiki/MapReduce) — distributed external sorting at scale
- [B-tree](https://en.wikipedia.org/wiki/B-tree) — disk-optimized data structure using similar I/O principles
- [Replacement selection sort](https://en.wikipedia.org/wiki/Replacement_selection_sorting) — alternative initial run generation (produces longer runs than naive chunking) — **implemented here**, opt-in via `SortOptions.UseReplacementSelection`
- [Polyphase merge sort](https://en.wikipedia.org/wiki/Polyphase_merge_sort) — optimized tape merge schedule
- [Tournament sort](https://en.wikipedia.org/wiki/Tournament_sort) — alternative to binary heap for k-way merge

## License

MIT
