# External Sorting

Enterprise-grade k-way external merge sort in .NET 8. Sorts datasets larger than available RAM using disk-based chunking and multi-way merge with a binary min-heap.

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

### Algorithm

1. **Chunk Phase**: Read input in chunks that fit in memory → sort each chunk in-memory → write to temp file
2. **Merge Phase**: K-way merge using binary min-heap. Each pass merges K chunks into 1. Repeat until single sorted file remains.
3. **Cleanup**: All temp files cleaned up automatically (even on failure)

**Complexity**: O(N log N) comparisons, O(N/M × log(N/M) / log(K)) I/O passes

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

| Records | Memory | Merge | Time | Verified |
|---------|--------|-------|------|----------|
| 100K | 8 MB | 4-way | 0.1s | OK |
| 1M | 16 MB | 8-way | 1.3s | OK |

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
