using System.Diagnostics;
using ExternalSorting.Core;
using ExternalSorting.Core.IO;
using ExternalSorting.Core.Pipeline;

const long DefaultCount = 1_000_000;
const long DefaultMemoryMb = 64;
const int DefaultMergeWay = 8;

// Parse CLI arguments
long count = DefaultCount;
long memoryMb = DefaultMemoryMb;
int mergeWay = DefaultMergeWay;
string? inputPath = null;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "-n" or "--count" when i + 1 < args.Length:
            count = long.Parse(args[++i]);
            break;
        case "-m" or "--memory" when i + 1 < args.Length:
            memoryMb = long.Parse(args[++i]);
            break;
        case "-k" or "--merge-way" when i + 1 < args.Length:
            mergeWay = int.Parse(args[++i]);
            break;
        case "-i" or "--input" when i + 1 < args.Length:
            inputPath = args[++i];
            break;
        case "-h" or "--help":
            Console.WriteLine("External Sorting — Enterprise k-way merge sort");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  -n, --count <N>      Number of records to generate (default: 1M)");
            Console.WriteLine("  -m, --memory <MB>    Memory budget in MB (default: 64)");
            Console.WriteLine("  -k, --merge-way <K>  K-way merge factor (default: 8)");
            Console.WriteLine("  -i, --input <file>   Use existing input file (skip generation)");
            Console.WriteLine("  -h, --help           Show this help");
            return;
    }
}

var serializer = new RecordSerializer();
var comparer = Comparer<SortRecord>.Default;
var tempDir = Path.Combine(Path.GetTempPath(), "external_sort");
Directory.CreateDirectory(tempDir);

// Step 1: Generate or use existing input
string dataPath = inputPath ?? Path.Combine(tempDir, "input.bin");
if (inputPath is null)
{
    Console.Write($"Generating {count:N0} records... ");
    var genSw = Stopwatch.StartNew();
    DataGenerator.GenerateBinary(dataPath, count);
    Console.WriteLine($"done in {genSw.Elapsed.TotalSeconds:F1}s ({new FileInfo(dataPath).Length / 1024.0 / 1024.0:F1} MB)");
}
else
{
    Console.WriteLine($"Using input: {inputPath} ({new FileInfo(inputPath).Length / 1024.0 / 1024.0:F1} MB)");
}

// Step 2: Sort
string outputPath = Path.Combine(tempDir, "output.bin");

var options = new SortOptions
{
    MaxMemoryBytes = memoryMb * 1024 * 1024,
    MergeWayCount = mergeWay,
    TempDirectory = tempDir,
    OnProgress = (phase, pct) =>
    {
        string pctStr = pct >= 0 ? $" {pct:F0}%" : "";
        Console.Write($"\r  [{phase}]{pctStr}     ");
    },
};

var sorter = new ExternalSorter<SortRecord>(serializer, comparer, options);

Console.WriteLine($"Sorting (memory: {memoryMb} MB, merge: {mergeWay}-way)...");
var sortSw = Stopwatch.StartNew();

using (var input = File.OpenRead(dataPath))
using (var output = File.Create(outputPath))
{
    sorter.Sort(input, output);
}

Console.WriteLine();
Console.WriteLine($"Sort completed in {sortSw.Elapsed.TotalSeconds:F2}s");

if (sorter.LastMetrics is { } m)
    Console.WriteLine($"  {m}");

// Step 3: Verify
Console.Write("Verifying sort order... ");
long verified = 0;
bool isSorted = true;

using (var fs = File.OpenRead(outputPath))
using (var br = new BinaryReader(fs))
{
    int itemCount = br.ReadInt32();
    SortRecord? prev = null;

    for (int i = 0; i < itemCount; i++)
    {
        var current = serializer.Read(br);
        if (prev.HasValue && current.CompareTo(prev.Value) < 0)
        {
            Console.WriteLine($"FAILED at index {i}: {TextRecordIO.Format(prev.Value)} > {TextRecordIO.Format(current)}");
            isSorted = false;
            break;
        }
        prev = current;
        verified++;
    }
}

if (isSorted)
    Console.WriteLine($"OK ({verified:N0} items in order)");

// Cleanup
if (inputPath is null)
    File.Delete(dataPath);
File.Delete(outputPath);
