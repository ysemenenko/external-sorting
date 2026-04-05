namespace ExternalSorting.Core;

/// <summary>Serialize/deserialize items to/from streams.</summary>
public interface ISerializer<T>
{
    void Write(BinaryWriter writer, T item);
    T Read(BinaryReader reader);

    /// <summary>Estimated size in bytes per item (for chunk size calculation).</summary>
    int EstimatedItemSize { get; }
}
