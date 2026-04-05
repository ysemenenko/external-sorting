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

    /// <summary>Progress callback: (phase, percentComplete).</summary>
    public Action<SortPhase, double>? OnProgress { get; init; }
}

public enum SortPhase
{
    ChunkCreation,
    Merging,
    Done,
}
