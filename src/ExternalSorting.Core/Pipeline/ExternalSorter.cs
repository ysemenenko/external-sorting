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
            var chunk = ChunkWriter.Write(buffer, _serializer, tempDir, chunkIndex);
            chunks.Add(chunk);

            metrics.TotalItems += buffer.Count;
            metrics.ChunksCreated++;
            chunkIndex++;

            _options.OnProgress?.Invoke(SortPhase.ChunkCreation, -1); // indeterminate
        }

        return chunks;
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

        while (heap.Count > 0)
        {
            var (item, readerIdx) = heap.ExtractMin();
            _serializer.Write(bw, item);
            totalItems++;

            if (readers[readerIdx].MoveNext())
                heap.Insert((readers[readerIdx].Current, readerIdx));
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
