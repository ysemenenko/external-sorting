using ExternalSorting.Core.IO;
using FluentAssertions;

namespace ExternalSorting.Tests;

public class RecordSerializerTests
{
    private readonly RecordSerializer _serializer = new();

    [Fact]
    public void Roundtrip_binary()
    {
        var record = new SortRecord(42, "Hello World");

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        _serializer.Write(bw, record);

        ms.Position = 0;
        using var br = new BinaryReader(ms);
        var result = _serializer.Read(br);

        result.Should().Be(record);
    }

    [Fact]
    public void Roundtrip_multiple()
    {
        var records = new SortRecord[]
        {
            new(1, "Apple"),
            new(999, "Banana"),
            new(0, "Cherry"),
        };

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        foreach (var r in records)
            _serializer.Write(bw, r);

        ms.Position = 0;
        using var br = new BinaryReader(ms);
        for (int i = 0; i < records.Length; i++)
            _serializer.Read(br).Should().Be(records[i]);
    }

    [Fact]
    public void Text_parse_standard_format()
    {
        var record = TextRecordIO.Parse("42. Apple");
        record.Number.Should().Be(42);
        record.Text.Should().Be("Apple");
    }

    [Fact]
    public void Text_parse_no_dot()
    {
        var record = TextRecordIO.Parse("no dot here");
        record.Number.Should().Be(0);
        record.Text.Should().Be("no dot here");
    }

    [Fact]
    public void Text_format_roundtrip()
    {
        var record = new SortRecord(123, "Mango");
        var text = TextRecordIO.Format(record);
        var parsed = TextRecordIO.Parse(text);
        parsed.Should().Be(record);
    }

    [Fact]
    public void SortRecord_comparison_text_first()
    {
        var a = new SortRecord(100, "Apple");
        var b = new SortRecord(1, "Banana");
        a.CompareTo(b).Should().BeNegative(); // Apple < Banana
    }

    [Fact]
    public void SortRecord_comparison_number_tiebreaker()
    {
        var a = new SortRecord(1, "Apple");
        var b = new SortRecord(2, "Apple");
        a.CompareTo(b).Should().BeNegative(); // same text, lower number first
    }

    [Fact]
    public void SortRecord_comparison_equal()
    {
        var a = new SortRecord(1, "Apple");
        var b = new SortRecord(1, "Apple");
        a.CompareTo(b).Should().Be(0);
    }

    [Fact]
    public void EstimatedItemSize_is_positive()
    {
        _serializer.EstimatedItemSize.Should().BeGreaterThan(0);
    }
}
