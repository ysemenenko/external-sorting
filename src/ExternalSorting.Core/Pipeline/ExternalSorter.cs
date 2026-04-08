using System.Collections.Concurrent;
using System.Diagnostics;
using ExternalSorting.Core.IO;
using ExternalSorting.Core.Merge;

namespace ExternalSorting.Core.Pipeline;

/// <summary>
/// External merge sort: splits input into sorted chunks, then k-way merges
/// until a single sorted output remains.
/// </summary>
public sealed class ExternalSorter<T> : IExternalSorter<T>
{
    private readonly ISerializer<T> _serializer;
    private readonly IComparer<T> _comparer;
    private readonly SortOptions _options;

    public SortMetrics? LastMetrics { get; private set; }

    public ExternalSorter(ISerializer<T> serializer, IComparer<T> comparer, SortOptions? options = null)
    {
        _serializer = serializer;
        _comparer = comparer;
        _options = options ?? new SortOptions();
    }

    public void Sort(Stream input, Stream output, CancellationToken ct = default)
    {
        var metrics = new SortMetrics();

        // Create temp directory for this sort session
        string tempDir = Path.Combine(_options.TempDirectory, $"sort_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            // Phase 1: chunk creation
            var sw = Stopwatch.StartNew();
            var chunks = CreateChunks(input, tempDir, metrics, ct);
            metrics.ChunkPhaseTime = sw.Elapsed;

            if (chunks.Count == 0)
            {
                LastMetrics = metrics;
                return;
            }

            // Phase 2: merge passes
            sw.Restart();
            MergeChunks(chunks, output, tempDir, metrics, ct);
            metrics.MergePhaseTime = sw.Elapsed;

            _options.OnProgress?.Invoke(SortPhase.Done, 100);
            LastMetrics = metrics;
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
        }
    }

    private List<ChunkFile> CreateChunks(Stream input, string tempDir, SortMetrics metrics, CancellationToken ct)
    {
        // Dispatch (priority order):
        // 1. UseReplacementSelection → Knuth's algorithm, ~2x larger runs
        //    on average for random input. Inherently single-threaded.
        // 2. parallelism > 1 → pipelined producer/consumer chunking
        // 3. parallelism == 1 → original serial chunking, single recycled
        //    buffer (lowest GC, the regression baseline)
        if (_options.UseReplacementSelection)
            return CreateChunksReplacementSelection(input, tempDir, metrics, ct);

        int parallelism = Math.Max(1, _options.DegreeOfParallelism);
        if (parallelism == 1)
            return CreateChunksSerial(input, tempDir, metrics, ct);
        return CreateChunksParallel(input, tempDir, metrics, ct, parallelism);
    }

    private List<ChunkFile> CreateChunksReplacementSelection(
        Stream input, string tempDir, SortMetrics metrics, CancellationToken ct)
    {
        // Replacement Selection (Knuth, TAOCP Vol. 3, §5.4.1):
        //
        //   1. Fill a min-heap with M items from the input.
        //   2. Loop:
        //      a. Extract the heap min and write it to the current run.
        //      b. Read the next item from the input.
        //      c. If next ≥ just-emitted: insert into the *current* run
        //         (it can still extend the sorted output).
        //      d. Otherwise: insert into the *next* run, frozen until
        //         the current run is exhausted from the heap.
        //   3. When the current run drains, close that chunk file and
        //      start a new one for the next run.
        //
        // Average run size for random input is 2M (proven by Knuth);
        // worst case (descending input) degenerates to M-sized runs.
        //
        // Implementation trick: instead of two physical heaps, encode
        // the run number in the heap key as a (run, item) tuple. The
        // comparator orders by run first, then by item, so all run-N
        // items naturally come out before any run-(N+1) items.
        int heapCapacity = Math.Max(1, (int)(_options.MaxMemoryBytes / _serializer.EstimatedItemSize));

        var runComparer = Comparer<(int Run, T Item)>.Create((a, b) =>
        {
            int rc = a.Run.CompareTo(b.Run);
            return rc != 0 ? rc : _comparer.Compare(a.Item, b.Item);
        });

        var heap = new MinHeap<(int Run, T Item)>(runComparer, heapCapacity);
        var reader = new BinaryReader(input);
        var chunks = new List<ChunkFile>();
        bool eof = false;

        // Phase 1: prime the heap with up to heapCapacity items.
        // All initial items belong to run 0.
        for (int i = 0; i < heapCapacity; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                heap.Insert((0, _serializer.Read(reader)));
            }
            catch (EndOfStreamException)
            {
                eof = true;
                break;
            }
        }

        if (heap.Count == 0)
            return chunks;

        // Open-write helpers — we delay the first chunk file creation
        // until just before the first write so an empty input doesn't
        // leave a stub file behind.
        int currentRun = 0;
        int chunkIndex = 0;
        FileStream? fs = null;
        BinaryWriter? bw = null;
        int currentChunkCount = 0;

        void OpenChunk()
        {
            string path = Path.Combine(tempDir, $"chunk_rs_{chunkIndex:D6}.bin");
            fs = new FileStream(path, FileMode.Create, FileAccess.Write,
                                FileShare.None, _options.BufferSize);
            bw = new BinaryWriter(fs);
            bw.Write(0); // count placeholder
            currentChunkCount = 0;
        }

        void CloseChunk()
        {
            if (bw is null || fs is null)
                return;
            bw.Flush();
            fs.Position = 0;
            bw.Write(currentChunkCount);
            bw.Dispose();
            fs.Dispose();
            string path = Path.Combine(tempDir, $"chunk_rs_{chunkIndex:D6}.bin");
            chunks.Add(new ChunkFile(path, currentChunkCount));
            metrics.ChunksCreated++;
            chunkIndex++;
            bw = null;
            fs = null;
        }

        OpenChunk();

        while (heap.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (run, item) = heap.ExtractMin();

            if (run != currentRun)
            {
                // Run boundary: flush current chunk, start a new one
                // for the new run number.
                CloseChunk();
                currentRun = run;
                OpenChunk();
            }

            _serializer.Write(bw!, item);
            currentChunkCount++;
            metrics.TotalItems++;

            if (!eof)
            {
                T next;
                try
                {
                    next = _serializer.Read(reader);
                }
                catch (EndOfStreamException)
                {
                    eof = true;
                    continue;
                }
                // The just-emitted *item* is the freshest "last output"
                // for this run, so the comparison is against it.
                if (_comparer.Compare(next, item) >= 0)
                    heap.Insert((currentRun, next));
                else
                    heap.Insert((currentRun + 1, next));
            }

            _options.OnProgress?.Invoke(SortPhase.ChunkCreation, -1);
        }

        // Final flush — whatever chunk was open at the time the heap
        // emptied is the last one. CloseChunk is idempotent (no-op
        // when bw/fs are null) so this is safe even if the loop body
        // already closed the trailing chunk.
        CloseChunk();
        return chunks;
    }

    private List<ChunkFile> CreateChunksSerial(Stream input, string tempDir, SortMetrics metrics, CancellationToken ct)
    {
        var chunks = new List<ChunkFile>();
        int chunkCapacity = Math.Max(1, (int)(_options.MaxMemoryBytes / _serializer.EstimatedItemSize));
        var buffer = new List<T>(chunkCapacity);
        var reader = new BinaryReader(input);
        int chunkIndex = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            buffer.Clear();

            // Fill buffer up to capacity
            for (int i = 0; i < chunkCapacity; i++)
            {
                try
                {
                    var item = _serializer.Read(reader);
                    buffer.Add(item);
                }
                catch (EndOfStreamException)
                {
                    break;
                }
            }

            if (buffer.Count == 0)
                break;

            // Sort in memory
            buffer.Sort(_comparer);

            // Write chunk to disk
            var chunk = ChunkWriter.Write(buffer, _serializer, tempDir, chunkIndex, _options.BufferSize);
            chunks.Add(chunk);

            metrics.TotalItems += buffer.Count;
            metrics.ChunksCreated++;
            chunkIndex++;

            _options.OnProgress?.Invoke(SortPhase.ChunkCreation, -1); // indeterminate
        }

        return chunks;
    }

    private List<ChunkFile> CreateChunksParallel(
        Stream input, string tempDir, SortMetrics metrics,
        CancellationToken ct, int parallelism)
    {
        // Pipelined chunk creation: one reader thread streams items off
        // *input* and hands fully-filled buffers to a worker pool that
        // sorts each buffer and writes the chunk to disk in parallel.
        // The bounded BlockingCollection caps in-flight buffers at
        // parallelism*2 so we don't run away from MaxMemoryBytes by
        // an unbounded amount under producer-faster-than-consumers.
        //
        // Memory note: peak RAM during this phase is approximately
        // (parallelism+1) * (MaxMemoryBytes / EstimatedItemSize) items
        // — one buffer being filled by the reader, and up to parallelism
        // more being processed by workers. Document this in SortOptions
        // if you need to dial it back further.
        int chunkCapacity = Math.Max(1, (int)(_options.MaxMemoryBytes / _serializer.EstimatedItemSize));

        // Linked CTS so a worker can fault the pipeline and unblock the
        // reader (otherwise a worker dying with disk-full would leave
        // the reader blocked on queue.Add forever).
        using var workerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var combinedCt = workerCts.Token;

        using var queue = new BlockingCollection<(int Index, List<T> Buffer)>(
            boundedCapacity: parallelism * 2);
        var resultBag = new ConcurrentBag<ChunkFile>();

        var workers = new Task[parallelism];
        for (int w = 0; w < parallelism; w++)
        {
            workers[w] = Task.Run(() =>
            {
                try
                {
                    foreach (var (idx, buf) in queue.GetConsumingEnumerable(combinedCt))
                    {
                        buf.Sort(_comparer);
                        var chunk = ChunkWriter.Write(
                            buf, _serializer, tempDir, idx, _options.BufferSize);
                        resultBag.Add(chunk);
                    }
                }
                catch
                {
                    // Cancel siblings + reader so the pipeline tears
                    // down cleanly instead of deadlocking on queue.Add.
                    workerCts.Cancel();
                    throw;
                }
            });
        }

        var reader = new BinaryReader(input);
        int chunkIndex = 0;
        try
        {
            while (true)
            {
                combinedCt.ThrowIfCancellationRequested();
                // Allocate a fresh buffer per chunk — workers process
                // them concurrently so we cannot reuse the serial path's
                // single recycled buffer.
                var buffer = new List<T>(chunkCapacity);
                for (int i = 0; i < chunkCapacity; i++)
                {
                    try
                    {
                        buffer.Add(_serializer.Read(reader));
                    }
                    catch (EndOfStreamException)
                    {
                        break;
                    }
                }
                if (buffer.Count == 0)
                    break;

                int count = buffer.Count;
                queue.Add((chunkIndex++, buffer), combinedCt);
                metrics.TotalItems += count;
                metrics.ChunksCreated++;
                _options.OnProgress?.Invoke(SortPhase.ChunkCreation, -1);
            }
        }
        finally
        {
            queue.CompleteAdding();
        }

        try
        {
            Task.WaitAll(workers);
        }
        catch (AggregateException ae)
        {
            // Surface the first inner exception so the caller still
            // sees OperationCanceledException / IOException etc., not
            // a generic AggregateException wrapper.
            if (ae.InnerExceptions.Count == 1)
                throw ae.InnerExceptions[0];
            throw;
        }

        return resultBag.ToList();
    }

    private void MergeChunks(List<ChunkFile> chunks, Stream output, string tempDir,
                             SortMetrics metrics, CancellationToken ct)
    {
        int mergeWay = _options.MergeWayCount;
        int passIndex = 0;

        // Multi-pass merge until single output
        while (chunks.Count > 1)
        {
            ct.ThrowIfCancellationRequested();

            var nextChunks = new List<ChunkFile>();

            for (int i = 0; i < chunks.Count; i += mergeWay)
            {
                ct.ThrowIfCancellationRequested();

                int end = Math.Min(i + mergeWay, chunks.Count);
                var batch = chunks.GetRange(i, end - i);

                if (batch.Count == 1)
                {
                    nextChunks.Add(batch[0]);
                    continue;
                }

                var merged = MergeBatch(batch, tempDir, passIndex, i / mergeWay);
                nextChunks.Add(merged);

                // Dispose source chunks (delete temp files)
                foreach (var c in batch)
                    c.Dispose();
            }

            chunks = nextChunks;
            metrics.MergePasses++;
            passIndex++;

            double pct = chunks.Count == 1 ? 100.0 : 50.0; // rough estimate
            _options.OnProgress?.Invoke(SortPhase.Merging, pct);
        }

        // Write final chunk to output
        if (chunks.Count == 1)
        {
            WriteFinalOutput(chunks[0], output);
            chunks[0].Dispose();
        }
    }

    private ChunkFile MergeBatch(List<ChunkFile> batch, string tempDir, int pass, int batchIndex)
    {
        string outPath = Path.Combine(tempDir, $"merge_p{pass}_b{batchIndex:D4}.bin");

        // Open readers for all chunks
        var readers = batch.Select(c => new ChunkReader<T>(c, _serializer, _options.BufferSize)).ToList();

        // Initialize heap
        var heap = new MinHeap<(T Item, int ReaderIndex)>(
            Comparer<(T Item, int ReaderIndex)>.Create((a, b) => _comparer.Compare(a.Item, b.Item)),
            readers.Count
        );

        // Seed heap with first item from each reader
        for (int r = 0; r < readers.Count; r++)
        {
            if (readers[r].MoveNext())
                heap.Insert((readers[r].Current, r));
        }

        int totalItems = 0;
        using var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None, _options.BufferSize);
        using var bw = new BinaryWriter(fs);

        // Placeholder for count header
        bw.Write(0);

        // K-way merge with ReplaceMin fast-path: when the reader that
        // just yielded the min still has more data, we can overwrite
        // the heap root in place (one SiftDown) instead of doing
        // ExtractMin + Insert (SiftDown + SiftUp). This saves the
        // SiftUp half of the work for every item except the last one
        // from each input — i.e. ~50% of the heap ops at the bottom
        // of the merge phase, where this loop is the hot path.
        while (heap.Count > 0)
        {
            var (item, readerIdx) = heap.Peek();
            _serializer.Write(bw, item);
            totalItems++;

            if (readers[readerIdx].MoveNext())
                heap.ReplaceMin((readers[readerIdx].Current, readerIdx));
            else
                heap.ExtractMin();
        }

        // Patch header with actual count
        bw.Flush();
        fs.Position = 0;
        bw.Write(totalItems);

        // Cleanup readers
        foreach (var r in readers)
            r.Dispose();

        return new ChunkFile(outPath, totalItems);
    }

    private void WriteFinalOutput(ChunkFile chunk, Stream output)
    {
        using var reader = new ChunkReader<T>(chunk, _serializer, _options.BufferSize);
        using var bw = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true);

        // Write header
        bw.Write(chunk.ItemCount);

        while (reader.MoveNext())
            _serializer.Write(bw, reader.Current);
    }
}
