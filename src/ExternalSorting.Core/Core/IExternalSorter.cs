namespace ExternalSorting.Core;

/// <summary>External sort: input stream → sorted output stream.</summary>
public interface IExternalSorter<T>
{
    /// <summary>
    /// Sort items from <paramref name="input"/> to <paramref name="output"/>
    /// using disk-based merge sort.
    /// </summary>
    /// <remarks>
    /// <para><b>Stream ownership:</b> the caller owns both streams; this method
    /// never closes them. It reads <paramref name="input"/> sequentially from
    /// its current position to end-of-stream, and writes the sorted result to
    /// <paramref name="output"/> as an <see cref="long"/> count header followed
    /// by that many serialized items.</para>
    /// <para><b>Stability:</b> the sort is <i>not</i> stable — equal elements
    /// may be reordered relative to their input order (the in-memory chunk sort
    /// and the k-way merge break ties only by the comparer, not by input
    /// position). Make the comparer total if a deterministic order of equal
    /// keys matters.</para>
    /// </remarks>
    void Sort(Stream input, Stream output, CancellationToken ct = default);
}
