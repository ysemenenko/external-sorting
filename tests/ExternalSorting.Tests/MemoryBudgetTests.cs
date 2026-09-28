using ExternalSorting.Core;
using ExternalSorting.Core.Pipeline;
using FluentAssertions;

namespace ExternalSorting.Tests;

[CollectionDefinition("Memory budget concurrency", DisableParallelization = true)]
public sealed class MemoryBudgetCollection { }

[Collection("Memory budget concurrency")]
public sealed class MemoryBudgetTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"budget_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
    }

    [Theory]
    [InlineData(1, 8)] // less than one item: retain the one-item minimum
    [InlineData(8, 8)]
    [InlineData(8, int.MaxValue)] // worker/buffer arithmetic must not overflow
    [InlineData(16, 8)] // fewer slots than workers
    [InlineData(88, 4)] // division leaves one unused slot
    [InlineData(512, 4)]
    public async Task Blocked_workers_cannot_read_beyond_global_budget(long budget, int dop)
    {
        using var release = new ManualResetEventSlim();
        using var exhausted = new ManualResetEventSlim();
        using var input = Input();
        using var output = new MemoryStream();
        var serializer = new ProbeSerializer(input, () => release.Wait(TimeSpan.FromSeconds(10)));
        int slots = (int)Math.Max(1, budget / serializer.EstimatedItemSize);
        int buffers = (int)Math.Min(slots, (long)dop + 1);
        int submitted = 0;
        var sorter = Sorter(serializer, budget, dop, (phase, _) =>
        {
            if (phase == SortPhase.ChunkCreation && ++submitted == buffers) exhausted.Set();
        });
        var task = Task.Factory.StartNew(() => sorter.Sort(input, output),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            exhausted.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
            // Every buffer is now owned by the queue or a blocked writer.
            await Task.Delay(100);
            Volatile.Read(ref serializer.InputReads).Should().Be(buffers * (slots / buffers));
            task.IsCompleted.Should().BeFalse();
        }
        finally
        {
            release.Set();
            await task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        output.Position = 0;
        using var reader = new BinaryReader(output);
        reader.ReadInt64().Should().Be(200);
        Enumerable.Range(0, 200).Select(_ => reader.ReadInt32()).Should().Equal(Enumerable.Range(1, 200));
        Directory.GetDirectories(_tempDir).Should().BeEmpty();
    }

    [Fact]
    public async Task Cancellation_while_waiting_for_buffer_drains_workers()
    {
        using var release = new ManualResetEventSlim();
        using var writing = new ManualResetEventSlim();
        using var submitted = new ManualResetEventSlim();
        using var cts = new CancellationTokenSource();
        using var input = Input();
        using var output = new MemoryStream();
        var serializer = new ProbeSerializer(input, () =>
        {
            writing.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
        });
        // Only one buffer fits. Once submitted, the reader must wait for it.
        var sorter = Sorter(serializer, 8, 4, (phase, _) =>
        {
            if (phase == SortPhase.ChunkCreation) submitted.Set();
        });
        var task = Task.Factory.StartNew(() => sorter.Sort(input, output, cts.Token),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            submitted.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
            writing.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
            cts.Cancel();
            await Task.Delay(100);
            task.IsCompleted.Should().BeFalse("the writer must be joined before returning");
            Volatile.Read(ref serializer.InputReads).Should().Be(1);
        }
        finally
        {
            cts.Cancel();
            release.Set();
            var error = await Record.ExceptionAsync(async () => await task.WaitAsync(TimeSpan.FromSeconds(15)));
            error.Should().BeAssignableTo<OperationCanceledException>();
        }
        Directory.GetDirectories(_tempDir).Should().BeEmpty();
    }

    [Fact]
    public async Task Chunk_sorting_remains_parallel()
    {
        using var sorting = new CountdownEvent(2);
        using var input = Input();
        using var output = new MemoryStream();
        var threads = new System.Collections.Concurrent.ConcurrentDictionary<int, byte>();
        var comparer = Comparer<int>.Create((a, b) =>
        {
            if (threads.TryAdd(Environment.CurrentManagedThreadId, 0) && !sorting.IsSet)
            {
                sorting.Signal();
                if (!sorting.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Sorts did not overlap");
            }
            return a.CompareTo(b);
        });
        var sorter = new ExternalSorter<int>(new ProbeSerializer(input), comparer,
            new SortOptions { MaxMemoryBytes = 96, DegreeOfParallelism = 2, TempDirectory = _tempDir });
        await Task.Run(() => sorter.Sort(input, output)).WaitAsync(TimeSpan.FromSeconds(30));
        threads.Count.Should().BeGreaterThanOrEqualTo(2);
    }

    [Theory]
    [InlineData("write")]
    [InlineData("read")]
    [InlineData("compare")]
    [InlineData("progress")]
    [InlineData("cancel")]
    public async Task Faults_and_cancellation_release_pipeline(string failure)
    {
        using var input = Input();
        using var output = new MemoryStream();
        using var cts = new CancellationTokenSource();
        var serializer = new ProbeSerializer(input, () =>
        {
            if (failure == "write") throw new IOException("write failure");
            if (failure == "cancel") cts.Cancel();
        }, failure == "read");
        var comparer = Comparer<int>.Create((a, b) => failure == "compare"
            ? throw new InvalidOperationException("compare failure") : a.CompareTo(b));
        var sorter = new ExternalSorter<int>(serializer, comparer, new SortOptions
        {
            MaxMemoryBytes = 80, DegreeOfParallelism = 4, TempDirectory = _tempDir,
            OnProgress = (phase, _) =>
            {
                if (phase == SortPhase.ChunkCreation && failure == "progress")
                    throw new IOException("progress failure");
            }
        });
        var error = await Record.ExceptionAsync(async () =>
            await Task.Run(() => sorter.Sort(input, output, cts.Token)).WaitAsync(TimeSpan.FromSeconds(15)));
        error.Should().NotBeNull().And.NotBeOfType<TimeoutException>();
        if (failure == "cancel") error.Should().BeAssignableTo<OperationCanceledException>();
        else error!.ToString().Should().Contain(failure + " failure");
        Directory.GetDirectories(_tempDir).Should().BeEmpty();
        input.CanRead.Should().BeTrue();
        output.CanWrite.Should().BeTrue();
    }

    private ExternalSorter<int> Sorter(ISerializer<int> serializer, long budget, int dop,
        Action<SortPhase, double> progress) => new(serializer, Comparer<int>.Default,
            new SortOptions { MaxMemoryBytes = budget, DegreeOfParallelism = dop,
                TempDirectory = _tempDir, OnProgress = progress });

    private static MemoryStream Input()
    {
        var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        for (int i = 200; i > 0; i--) writer.Write(i);
        writer.Flush();
        stream.Position = 0;
        return stream;
    }

    private sealed class ProbeSerializer(Stream input, Action? beforeWrite = null, bool failRead = false) : ISerializer<int>
    {
        public int InputReads;
        public int EstimatedItemSize => 8;

        public int Read(BinaryReader reader)
        {
            int item = reader.ReadInt32();
            if (ReferenceEquals(reader.BaseStream, input))
            {
                int count = Interlocked.Increment(ref InputReads);
                if (failRead && count == 7) throw new IOException("read failure");
            }
            return item;
        }

        public void Write(BinaryWriter writer, int item)
        {
            beforeWrite?.Invoke();
            writer.Write(item);
        }
    }
}
