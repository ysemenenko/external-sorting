namespace ExternalSorting.Core;

/// <summary>External sort: input stream → sorted output stream.</summary>
public interface IExternalSorter<T>
{
    /// <summary>Sort items from input to output using disk-based merge sort.</summary>
    void Sort(Stream input, Stream output, CancellationToken ct = default);
}
