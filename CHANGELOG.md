# Changelog

All notable changes to **ExternalSorting.Core** are documented here.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.5] — 2026-06-27

### Changed
- **Final output is now a verbatim byte copy.** The last merged chunk is
  already in the exact output format (`[int64 count][item]...`), so `Sort()`
  copies its bytes straight to the output stream instead of deserializing and
  re-serializing every record. This removes a whole pass of per-record
  (string) allocation from the hot path.
  - Measured on the 50K end-to-end benchmark (Ryzen 9800X3D): allocation
    **~23.75 MB → ~21.8 MB** (~8% less), and ~9–14% faster end-to-end
    (e.g. P=8 **15.41 ms → 13.30 ms**). Output is byte-identical
    (`Read ∘ Write` round-trips the format exactly).

## [1.0.4] — 2026-06-27

### Added
- **Parallel multi-pass merge.** The independent per-pass batches now merge
  concurrently (bounded by `DegreeOfParallelism`), so both phases scale.
  End-to-end speedup went from **~1.20× to ~1.54×** on a Ryzen 9800X3D (same
  box, same config — only the merge implementation changed); sweet spot moved
  from P=4 to the P=4–8 plateau, ~−24% wall-clock at P=8. The previous serial
  merge was the real ceiling (Amdahl's law), not memory bandwidth or cores.
- **Real `SortMetrics.BytesRead` / `BytesWritten`** — previously dead fields.
  Cheap `Position` delta for seekable streams, counting-stream fallback for
  non-seekable ones (no hot-path cost in the common case).
- `CHANGELOG.md` (this file).
- 20 new tests (suite **51 → 71**): option/null validation, fault cleanup
  across all three chunk strategies, a cross-platform proof that the parallel
  fault path drains blocked workers, Int64-header round-trip, truncated-input
  EOF handling, `leaveOpen`, metrics, and a deep multi-pass parallel-merge
  byte-identical check.

### Changed
- **On-disk count headers are now `Int64`** (chunk files *and* the final output
  header) so sorts beyond `int.MaxValue` items no longer corrupt counts.
- Documented stream-ownership and unstable-sort semantics on `IExternalSorter`,
  and the binary record format on `RecordSerializer`.
- Benchmarks re-measured on Ryzen 9800X3D (8c/16t); `DegreeOfParallelism`
  sweep extended to P=16; README now shows a serial-vs-parallel merge
  before/after table.

### Fixed
- **Option validation** in the constructor: non-null serializer/comparer,
  `MergeWayCount >= 2` (a 1-way merge never converged — was an **infinite
  loop**), positive memory/buffer, `DegreeOfParallelism >= 1`, non-empty temp
  directory, positive `EstimatedItemSize`; `Sort()` null-guards its streams.
- **Resource leaks / stream ownership.** Input readers use `leaveOpen: true`
  and are disposed; merge readers and replacement-selection writers are
  released in `finally` on fault; the chunk-capacity computation is clamped to
  `[1, int.MaxValue]` to avoid overflow / zero-size buffers.
- **Parallel pipeline fault handling.** A reader-thread fault now cancels and
  drains the workers before propagating (no worker writes into a temp dir that
  is being deleted); a worker fault is no longer masked by the reader's induced
  cancellation; single inner exceptions rethrow via `ExceptionDispatchInfo`
  (stack preserved).

### Compatibility
- The chunk format and the final output header are now `Int64` (8-byte) counts.
  If you previously persisted sorter output and parse the count header
  yourself, read it as `Int64`.

## [1.0.3] — 2026-04-08

### Added
- **Replacement Selection** chunk strategy (Knuth, TAOCP Vol. 3 §5.4.1) —
  ~2× larger runs on average for random input → fewer chunks, fewer merge
  passes (`SortOptions.UseReplacementSelection`).
- **Pipelined parallel chunk creation** honoring `DegreeOfParallelism` — a
  reader thread feeds a bounded queue of worker sorters.
- **BenchmarkDotNet** project (`tests/ExternalSorting.Benchmarks`) covering the
  merge inner loop, chunk strategies, and the parallelism sweep.
- Correctness, race, and cancellation tests for parallel chunk creation.

### Changed
- **`ReplaceMin` fast path** in the k-way merge hot loop — overwrite the heap
  root in place (one SiftDown) instead of `ExtractMin + Insert`, ~26–34% faster
  on the merge inner loop.

## [1.0.2] — 2026-04-07

### Fixed
- Corrected `PackageProjectUrl` to the GitHub Pages site.

## [1.0.1] — 2026-04-07

### Added
- NuGet packaging: package metadata, README badge, and install instructions.

## [1.0.0] — 2026-04-05

### Added
- Initial release: generic `IExternalSorter<T>` k-way external merge sort for
  .NET 8 — bounded-memory chunking, a binary min-heap merge, pluggable
  `ISerializer<T>` / `IComparer<T>`, `SortOptions` / `SortMetrics`, and the
  classic "sort 1 GB with 1 MB RAM" demonstration.

[1.0.5]: https://github.com/ysemenenko/external-sorting/releases/tag/v1.0.5
[1.0.4]: https://github.com/ysemenenko/external-sorting/releases/tag/v1.0.4
[1.0.3]: https://github.com/ysemenenko/external-sorting/releases/tag/v1.0.3
[1.0.2]: https://github.com/ysemenenko/external-sorting/releases/tag/v1.0.2
[1.0.1]: https://github.com/ysemenenko/external-sorting/releases/tag/v1.0.1
[1.0.0]: https://github.com/ysemenenko/external-sorting/releases/tag/v1.0.0
