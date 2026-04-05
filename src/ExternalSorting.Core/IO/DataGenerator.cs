namespace ExternalSorting.Core.IO;

/// <summary>Generates random test data files.</summary>
public static class DataGenerator
{
    private static readonly string[] Words =
    [
        "Apple", "Banana", "Cherry", "Date", "Elderberry", "Fig", "Grape",
        "Honeydew", "Kiwi", "Lemon", "Mango", "Nectarine", "Orange",
        "Papaya", "Quince", "Raspberry", "Strawberry", "Tangerine",
        "Watermelon", "Zucchini", "Testimony", "Foundation", "Landscape",
        "Innovation", "Discovery", "Adventure", "Symphony", "Reflection",
        "Integrity", "Momentum", "Catalyst", "Precision",
    ];

    /// <summary>Generate a binary file with random SortRecords.</summary>
    public static void GenerateBinary(string path, long count, int bufferSize = 64 * 1024)
    {
        var rng = new Random(42); // deterministic for reproducibility
        var serializer = new RecordSerializer();

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize);
        using var bw = new BinaryWriter(fs);

        for (long i = 0; i < count; i++)
        {
            ulong number = (ulong)rng.NextInt64(0, long.MaxValue);
            string text = Words[rng.Next(Words.Length)];
            serializer.Write(bw, new SortRecord(number, text));
        }
    }

    /// <summary>Generate a text file with random records in "number. text" format.</summary>
    public static void GenerateText(string path, long count, int bufferSize = 64 * 1024)
    {
        var rng = new Random(42);

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize);
        using var sw = new StreamWriter(fs);

        for (long i = 0; i < count; i++)
        {
            ulong number = (ulong)rng.NextInt64(0, long.MaxValue);
            string text = Words[rng.Next(Words.Length)];
            sw.WriteLine($"{number}. {text}");
        }
    }
}
