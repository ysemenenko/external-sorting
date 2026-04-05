namespace ExternalSorting.Core;

public sealed class SortMetrics
{
    public long TotalItems { get; set; }
    public int ChunksCreated { get; set; }
    public int MergePasses { get; set; }
    public TimeSpan ChunkPhaseTime { get; set; }
    public TimeSpan MergePhaseTime { get; set; }
    public TimeSpan TotalTime => ChunkPhaseTime + MergePhaseTime;
    public long BytesRead { get; set; }
    public long BytesWritten { get; set; }

    public override string ToString() =>
        $"Items: {TotalItems:N0}, Chunks: {ChunksCreated}, " +
        $"Merge passes: {MergePasses}, " +
        $"Chunk phase: {ChunkPhaseTime.TotalSeconds:F1}s, " +
        $"Merge phase: {MergePhaseTime.TotalSeconds:F1}s, " +
        $"Total: {TotalTime.TotalSeconds:F1}s";
}
