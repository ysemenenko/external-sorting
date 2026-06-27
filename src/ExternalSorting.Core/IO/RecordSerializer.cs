namespace ExternalSorting.Core.IO;

/// <summary>A sortable record: number + text (compatible with legacy format).</summary>
public readonly record struct SortRecord(ulong Number, string Text) : IComparable<SortRecord>
{
    public int CompareTo(SortRecord other)
    {
        int cmp = string.Compare(Text, other.Text, StringComparison.Ordinal);
        return cmp != 0 ? cmp : Number.CompareTo(other.Number);
    }
}

/// <summary>
/// Binary serializer for <see cref="SortRecord"/>.
/// <para>Wire format per record: an 8-byte little-endian <see cref="ulong"/>
/// (<see cref="SortRecord.Number"/>) followed by a length-prefixed UTF-8 string
/// (<see cref="BinaryWriter.Write(string)"/> — a 7-bit-encoded byte length then
/// the UTF-8 bytes). <see cref="Read"/> throws <see cref="EndOfStreamException"/>
/// on a truncated record. The sorter's chunk-creation paths catch a trailing
/// <see cref="EndOfStreamException"/> on the caller's input, so a partially
/// written final record is dropped rather than throwing. (Chunk files written
/// by the sorter stay count-header strict — they are never truncated.)</para>
/// </summary>
public sealed class RecordSerializer : ISerializer<SortRecord>
{
    public int EstimatedItemSize => 8 + 40; // ulong + avg string

    public void Write(BinaryWriter writer, SortRecord item)
    {
        writer.Write(item.Number);
        writer.Write(item.Text);
    }

    public SortRecord Read(BinaryReader reader)
    {
        ulong number = reader.ReadUInt64();
        string text = reader.ReadString();
        return new SortRecord(number, text);
    }
}

/// <summary>Text format reader/writer for legacy "number. text" format.</summary>
public static class TextRecordIO
{
    public static SortRecord Parse(string line)
    {
        int dot = line.IndexOf('.');
        if (dot < 0)
            return new SortRecord(0, line.Trim());

        ulong.TryParse(line.AsSpan(0, dot).Trim(), out ulong number);
        string text = line.Substring(dot + 1).Trim();
        return new SortRecord(number, text);
    }

    public static string Format(SortRecord record) => $"{record.Number}. {record.Text}";
}
