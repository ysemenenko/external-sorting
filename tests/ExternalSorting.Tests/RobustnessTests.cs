using ExternalSorting.Core;
using ExternalSorting.Core.IO;
using ExternalSorting.Core.Pipeline;
using FluentAssertions;

namespace ExternalSorting.Tests;

/// <summary>
/// Hardening coverage for the robustness pass: option validation,
/// null guards, resource cleanup on fault, BytesRead/BytesWritten metrics,
/// the Int64 count-header format, and truncated-input handling. These exist
/// so a fault path CONFIRMS the fix rather than a user discovering it.
/// </summary>
public class RobustnessTests : IDisposable
{
    private readonly string _tempDir;
    private readonly RecordSerializer _serializer = new();

    public RobustnessTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sort_robust_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private SortOptions Opts(int mergeWay = 8, long mem = 4096, int dop = 1, bool rs = false) => new()
    {
        MaxMemoryBytes = mem,
        MergeWayCount = mergeWay,
        TempDirectory = _tempDir,
        DegreeOfParallelism = dop,
        UseReplacementSelection = rs,
    };

    private ExternalSorter<SortRecord> Sorter(SortOptions? o = null) =>
        new(_serializer, Comparer<SortRecord>.Default, o ?? Opts());

    // ---------------- constructor / option validation ----------------

    [Fact]
    public void Ctor_null_serializer_throws()
    {
        Action act = () => new ExternalSorter<SortRecord>(null!, Comparer<SortRecord>.Default);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_null_comparer_throws()
    {
        Action act = () => new ExternalSorter<SortRecord>(_serializer, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-3)]
    public void Ctor_mergeway_below_2_throws(int mergeWay)
    {
        Action act = () => Sorter(Opts(mergeWay: mergeWay));
        act.Should().Throw<ArgumentException>().WithMessage("*MergeWayCount*");
    }

    [Fact]
    public void Ctor_nonpositive_memory_throws()
    {
        Action act = () => Sorter(Opts(mem: 0));
        act.Should().Throw<ArgumentException>().WithMessage("*MaxMemoryBytes*");
    }

    [Fact]
    public void Ctor_nonpositive_buffer_throws()
    {
        Action act = () => Sorter(new SortOptions { BufferSize = 0, TempDirectory = _tempDir });
        act.Should().Throw<ArgumentException>().WithMessage("*BufferSize*");
    }

    [Fact]
    public void Ctor_zero_parallelism_throws()
    {
        Action act = () => Sorter(new SortOptions { DegreeOfParallelism = 0, TempDirectory = _tempDir });
        act.Should().Throw<ArgumentException>().WithMessage("*DegreeOfParallelism*");
    }

    [Fact]
    public void Ctor_empty_tempdir_throws()
    {
        Action act = () => Sorter(new SortOptions { TempDirectory = "  " });
        act.Should().Throw<ArgumentException>().WithMessage("*TempDirectory*");
    }

    [Fact]
    public void Ctor_bad_estimated_item_size_throws()
    {
        Action act = () => new ExternalSorter<SortRecord>(new BadSizeSerializer(), Comparer<SortRecord>.Default, Opts());
        act.Should().Throw<ArgumentException>().WithMessage("*EstimatedItemSize*");
    }

    [Fact]
    public void Sort_null_streams_throw()
    {
        var sorter = Sorter();
        using var ms = new MemoryStream();
        ((Action)(() => sorter.Sort(null!, ms))).Should().Throw<ArgumentNullException>();
        ((Action)(() => sorter.Sort(ms, null!))).Should().Throw<ArgumentNullException>();
    }

    // ---------------- metrics ----------------

    [Fact]
    public void Metrics_report_bytes_read_and_written()
    {
        var records = Enumerable.Range(0, 500)
            .Select(i => new SortRecord((ulong)(999 - i), $"k{i:D4}")).ToArray();
        using var input = WriteRecords(records);
        long inputLen = input.Length;
        using var output = new MemoryStream();

        var sorter = Sorter(Opts(mergeWay: 2, mem: 2048));
        sorter.Sort(input, output);

        // CountingStream tallies the real logical I/O volume.
        sorter.LastMetrics!.BytesRead.Should().Be(inputLen);
        sorter.LastMetrics!.BytesWritten.Should().Be(output.Length);
        sorter.LastMetrics!.BytesWritten.Should().BeGreaterThan(0);
    }

    // ---------------- Int64 count-header format ----------------

    [Fact]
    public void Output_uses_int64_count_header()
    {
        using var input = WriteRecords(new SortRecord(2, "b"), new SortRecord(1, "a"), new SortRecord(3, "c"));
        using var output = new MemoryStream();
        Sorter().Sort(input, output);

        output.Position = 0;
        long count = new BinaryReader(output).ReadInt64();
        count.Should().Be(3);
    }

    // ---------------- exception paths clean up temp + propagate ----------------

    [Theory]
    [InlineData(1)] // serial chunking
    [InlineData(4)] // parallel pipeline (fault is on the reader thread)
    public void Serializer_read_fault_propagates_and_cleans_temp(int dop)
    {
        var sorter = new ExternalSorter<SortRecord>(
            new FaultyReadSerializer(throwAfter: 50), Comparer<SortRecord>.Default, Opts(mem: 800, dop: dop));
        using var input = WriteRecords(Enumerable.Range(0, 300)
            .Select(i => new SortRecord((ulong)i, $"x{i:D4}")).ToArray());
        using var output = new MemoryStream();

        Action act = () => sorter.Sort(input, output);
        act.Should().Throw<InvalidOperationException>().WithMessage("*boom*");

        // The session dir is removed even though a chunk write was in flight.
        Directory.GetDirectories(_tempDir, "sort_*").Should().BeEmpty();
    }

    [Fact]
    public void Replacement_selection_read_fault_propagates_and_cleans_temp()
    {
        var sorter = new ExternalSorter<SortRecord>(
            new FaultyReadSerializer(throwAfter: 50), Comparer<SortRecord>.Default, Opts(mem: 800, rs: true));
        using var input = WriteRecords(Enumerable.Range(0, 300)
            .Select(i => new SortRecord((ulong)i, $"x{i:D4}")).ToArray());
        using var output = new MemoryStream();

        Action act = () => sorter.Sort(input, output);
        act.Should().Throw<InvalidOperationException>().WithMessage("*boom*");
        Directory.GetDirectories(_tempDir, "sort_*").Should().BeEmpty();
    }

    // ---------------- truncated trailing record = clean EOF ----------------

    [Fact]
    public void Truncated_trailing_record_is_dropped_not_thrown()
    {
        using var full = WriteRecords(new SortRecord(1, "a"), new SortRecord(2, "b"));
        var bytes = full.ToArray();
        using var truncated = new MemoryStream(bytes, 0, bytes.Length - 2); // chop last record
        using var output = new MemoryStream();

        Sorter().Sort(truncated, output);

        var result = ReadOutput(output);
        result.Should().HaveCount(1);
        result[0].Text.Should().Be("a");
    }

    // ---------------- stream ownership (leaveOpen) ----------------

    [Fact]
    public void Input_and_output_streams_are_not_closed()
    {
        using var input = WriteRecords(new SortRecord(1, "a"));
        using var output = new MemoryStream();
        Sorter().Sort(input, output);

        input.CanRead.Should().BeTrue();   // sorter must not dispose caller streams
        output.CanWrite.Should().BeTrue();
        output.Position.Should().BeGreaterThan(0);
    }

    // ---------------- parallel fault path drains workers ----------------

    // Cross-platform proof of the drain (unlinking open files is allowed on
    // Linux, so a "temp dir is gone" assertion can't prove it): block all
    // workers inside Write, cancel so the reader faults, and assert Sort()
    // does NOT return until the blocked workers are released.
    [Fact]
    public async Task Parallel_fault_path_drains_blocked_workers_before_returning()
    {
        using var started = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var ser = new BlockingWriteSerializer(started, release);

        using var cts = new CancellationTokenSource();
        using var input = WriteRecords(Enumerable.Range(0, 1000)
            .Select(i => new SortRecord((ulong)i, $"x{i:D5}")).ToArray());
        using var output = new MemoryStream();

        var sorter = new ExternalSorter<SortRecord>(ser, Comparer<SortRecord>.Default, Opts(mem: 800, dop: 4));
        var sortTask = Task.Run(() => sorter.Sort(input, output, cts.Token));

        try
        {
            // Workers have entered Write and are blocked; the reader is now
            // blocked on a full queue. (MRES.Wait is not a Task op.)
            started.Wait(5000).Should().BeTrue("a worker should pick up a chunk and start writing");

            // Cancel → the reader faults. The fault path must WAIT for the
            // blocked workers to finish rather than returning while they could
            // still be writing into tempDir.
            cts.Cancel();
            var firstDone = await Task.WhenAny(sortTask, Task.Delay(300));
            firstDone.Should().NotBeSameAs(sortTask, "Sort must drain the blocked workers before returning");
        }
        finally
        {
            release.Set(); // always unblock so the suite can never hang
        }

        Func<Task> complete = async () => await sortTask;
        await complete.Should().ThrowAsync<OperationCanceledException>();
    }

    // ---------------- parallel merge correctness ----------------

    // Deep multi-pass merge (mergeWay=2, tiny memory → many chunks/passes)
    // at high parallelism must be byte-identical to the single-threaded sort —
    // guards the now-concurrent per-pass batch merge.
    [Fact]
    public void Parallel_deep_merge_matches_serial_and_is_sorted()
    {
        var records = Enumerable.Range(0, 20000)
            .Select(i => new SortRecord((ulong)((i * 7919) % 20000), $"v{(i * 104729) % 50000:D5}"))
            .ToArray();

        byte[] Sort(int dop)
        {
            using var input = WriteRecords(records);
            using var output = new MemoryStream();
            new ExternalSorter<SortRecord>(_serializer, Comparer<SortRecord>.Default,
                Opts(mergeWay: 2, mem: 1024, dop: dop)).Sort(input, output);
            return output.ToArray();
        }

        var parallel = Sort(8);
        var serial = Sort(1);

        parallel.Should().Equal(serial, "parallel merge must produce byte-identical output");

        using var ms = new MemoryStream(parallel);
        var result = ReadOutput(ms);
        result.Should().HaveCount(records.Length);
        result.Should().BeInAscendingOrder(Comparer<SortRecord>.Default);
    }

    // ---------------- helpers ----------------

    private MemoryStream WriteRecords(params SortRecord[] records)
    {
        var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        foreach (var r in records) _serializer.Write(bw, r);
        bw.Flush();
        ms.Position = 0;
        return ms;
    }

    private List<SortRecord> ReadOutput(MemoryStream output)
    {
        output.Position = 0;
        var br = new BinaryReader(output);
        long count = br.ReadInt64();
        var result = new List<SortRecord>((int)count);
        for (long i = 0; i < count; i++) result.Add(_serializer.Read(br));
        return result;
    }

    private sealed class BadSizeSerializer : ISerializer<SortRecord>
    {
        public int EstimatedItemSize => 0;
        public void Write(BinaryWriter w, SortRecord item) { }
        public SortRecord Read(BinaryReader r) => default;
    }

    /// <summary>Delegates to a real serializer but throws after N reads.</summary>
    private sealed class FaultyReadSerializer : ISerializer<SortRecord>
    {
        private readonly RecordSerializer _inner = new();
        private readonly int _throwAfter;
        private int _count;

        public FaultyReadSerializer(int throwAfter) => _throwAfter = throwAfter;

        public int EstimatedItemSize => _inner.EstimatedItemSize;
        public void Write(BinaryWriter w, SortRecord item) => _inner.Write(w, item);

        public SortRecord Read(BinaryReader r)
        {
            if (Interlocked.Increment(ref _count) > _throwAfter)
                throw new InvalidOperationException("boom");
            return _inner.Read(r);
        }
    }

    /// <summary>Blocks every worker on its first Write until released.</summary>
    private sealed class BlockingWriteSerializer : ISerializer<SortRecord>
    {
        private readonly RecordSerializer _inner = new();
        private readonly ManualResetEventSlim _started;
        private readonly ManualResetEventSlim _release;

        public BlockingWriteSerializer(ManualResetEventSlim started, ManualResetEventSlim release)
        {
            _started = started;
            _release = release;
        }

        public int EstimatedItemSize => _inner.EstimatedItemSize;
        public SortRecord Read(BinaryReader r) => _inner.Read(r);

        public void Write(BinaryWriter w, SortRecord item)
        {
            if (!_release.IsSet)
            {
                _started.Set();
                _release.Wait();
            }
            _inner.Write(w, item);
        }
    }
}
