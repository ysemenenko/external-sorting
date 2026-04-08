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
        // Dispatch: serial path reuses one buffer (lowest GC), parallel
        // path runs a single reader thread feeding N sorter/writer
        // workers via a bounded BlockingCollection. P=1 falls through
        // to the original serial implementation byte-for-byte.
        int parallelism = Math.Max(1, _options.DegreeOfParallelism);
        if (parallelism == 1)
            return CreateChunksSerial(input, tempDir, metrics, ct);
        return CreateChunksParallel(input, tempDir, metrics, ct, parallelism);
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
