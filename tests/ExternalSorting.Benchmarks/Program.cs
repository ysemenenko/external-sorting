using BenchmarkDotNet.Running;

namespace ExternalSorting.Benchmarks;

// BenchmarkDotNet entry point. Run from the repo root in Release config:
//
//   dotnet run -c Release --project tests/ExternalSorting.Benchmarks
//
// Pick a single benchmark with --filter:
//
//   dotnet run -c Release --project tests/ExternalSorting.Benchmarks -- --filter '*MergeBatch*'
//   dotnet run -c Release --project tests/ExternalSorting.Benchmarks -- --filter '*ChunkPhase*'
//
// Results land in BenchmarkDotNet.Artifacts/results/ as Markdown + CSV
// + GitHub-friendly tables.
public static class Program
{
    public static void Main(string[] args)
        => BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
