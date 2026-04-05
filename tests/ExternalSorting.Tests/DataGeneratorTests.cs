using ExternalSorting.Core.IO;
using FluentAssertions;

namespace ExternalSorting.Tests;

public class DataGeneratorTests : IDisposable
{
    private readonly string _tempDir;

    public DataGeneratorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"gen_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public void GenerateBinary_creates_readable_file()
    {
        string path = Path.Combine(_tempDir, "test.bin");
        DataGenerator.GenerateBinary(path, 100);

        File.Exists(path).Should().BeTrue();
        new FileInfo(path).Length.Should().BeGreaterThan(0);

        var serializer = new RecordSerializer();
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);

        int count = 0;
        while (fs.Position < fs.Length)
        {
            var record = serializer.Read(br);
            record.Text.Should().NotBeNullOrEmpty();
            count++;
        }

        count.Should().Be(100);
    }

    [Fact]
    public void GenerateText_creates_readable_file()
    {
        string path = Path.Combine(_tempDir, "test.txt");
        DataGenerator.GenerateText(path, 50);

        var lines = File.ReadAllLines(path);
        lines.Should().HaveCount(50);

        foreach (var line in lines)
        {
            var record = TextRecordIO.Parse(line);
            record.Text.Should().NotBeNullOrEmpty();
        }
    }

    [Fact]
    public void GenerateBinary_deterministic_with_same_seed()
    {
        string path1 = Path.Combine(_tempDir, "a.bin");
        string path2 = Path.Combine(_tempDir, "b.bin");

        DataGenerator.GenerateBinary(path1, 100);
        DataGenerator.GenerateBinary(path2, 100);

        File.ReadAllBytes(path1).Should().Equal(File.ReadAllBytes(path2));
    }
}
