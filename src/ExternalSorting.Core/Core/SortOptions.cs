namespace ExternalSorting.Core;

public sealed class SortOptions
{
    /// <summary>Maximum memory (bytes) for in-memory chunk sorting. Default: 256 MB.</summary>
    public long MaxMemoryBytes { get; init; } = 256 * 1024 * 1024;

    /// <summary>Number of chunks merged per pass. Default: 8 (8-way merge).</summary>
    public int MergeWayCount { get; init; } = 8;

    /// <summary>Temp directory for intermediate files. Default: system temp.</summary>
    public string TempDirectory { get; init; } = Path.GetTempPath();

    /// <summary>I/O buffer size in bytes. Default: 64 KB.</summary>
    public int BufferSize { get; init; } = 64 * 1024;

    /// <summary>Degree of parallelism for chunk sorting. Default: processor count.</summary>
    public int DegreeOfParallelism { get; init; } = Environment.ProcessorCount;

    /// <summary>
    /// Use Replacement Selection during chunk creation. When true, the
    /// chunk phase runs Knuth's algorithm which produces runs that are
    /// on average twice the size of MaxMemoryBytes for random input.
    /// Fewer chunks → fewer merge passes → less total disk I/O. Trades
    /// CPU (more heap operations) for I/O. Default: false (simple
    /// fixed-size chunking).
    ///
    /// Mutually exclusive with parallelism: when true, DegreeOfParallelism
    /// is ignored because Replacement Selection is inherently single-
    /// threaded (the heap is shared across the entire input stream).
    /// </summary>
    public bool UseReplacementSelection { get; init; } = false;

    /// <summary>Progress callback: (phase, percentComplete).</summary>
    public Action<SortPhase, double>? OnProgress { get; init; }
}

public enum SortPhase
{
    ChunkCreation,
    Merging,
    Done,
}
