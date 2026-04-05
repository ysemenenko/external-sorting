# [External Sorting in C# / .NET 8](https://github.com/ysemenenko/external-sorting)

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
│   ├── SortOptions            — memory budget, merge factor, parallelism, temp dir
│   └── SortMetrics            — items, chunks, merge passes, timing
├── Pipeline/
│   └── ExternalSorter<T>     — orchestrator: chunk phase → merge phase
├── Merge/
│   └── MinHeap<T>            — O(log K) binary min-heap for k-way merge
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

## Quick Start

```bash
# Build
dotnet build

# Run tests (35 tests)
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
    OnProgress = (phase, pct) => Console.Write($"\r{phase} {pct:F0}%"),
};

var sorter = new ExternalSorter<SortRecord>(serializer, comparer, options);

using var input = File.OpenRead("input.bin");
using var output = File.Create("output.bin");
sorter.Sort(input, output);

Console.WriteLine(sorter.LastMetrics); // Items: 1,000,000, Chunks: 3, ...
```

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

35 tests covering:
- **MinHeap**: insert, extract, duplicates, replace, 10K random
- **Serializer**: binary roundtrip, text parse/format, comparison logic
- **Chunk I/O**: write/read roundtrip, empty, dispose cleanup, 10K items
- **ExternalSorter**: empty, single, sorted, reverse, duplicates, multi-chunk, multi-pass, 10K random, cancellation, temp cleanup, metrics
- **DataGenerator**: binary/text generation, deterministic seed

```bash
dotnet test --verbosity normal
```

## Project Structure

```
external-sorting/
├── ExternalSorting.sln
├── src/
│   ├── ExternalSorting.Core/       — library (algorithm + I/O)
│   └── ExternalSorting.Console/    — CLI application
└── tests/
    └── ExternalSorting.Tests/      — xUnit + FluentAssertions
```

## Key Design Decisions

- **Generic `T`**: Sort any type, not just strings — plug in your own `ISerializer<T>` and `IComparer<T>`
- **MinHeap merge**: O(N log K) vs old code's O(NK log K) — orders of magnitude faster for large K
- **Binary format**: 3-5x faster I/O than text parsing
- **Memory-adaptive chunking**: Chunk size computed from `MaxMemoryBytes / EstimatedItemSize`
- **Automatic cleanup**: Temp directory deleted in `finally` block, `ChunkFile` implements `IDisposable`
- **CancellationToken**: Cooperative cancellation at chunk and merge boundaries
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
- [Replacement selection sort](https://en.wikipedia.org/wiki/Replacement_selection_sorting) — alternative initial run generation (produces longer runs than naive chunking)
- [Polyphase merge sort](https://en.wikipedia.org/wiki/Polyphase_merge_sort) — optimized tape merge schedule
- [Tournament sort](https://en.wikipedia.org/wiki/Tournament_sort) — alternative to binary heap for k-way merge

## License

MIT
