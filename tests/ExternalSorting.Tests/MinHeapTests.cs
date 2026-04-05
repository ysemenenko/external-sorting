using ExternalSorting.Core.Merge;
using FluentAssertions;

namespace ExternalSorting.Tests;

public class MinHeapTests
{
    private MinHeap<int> CreateHeap() => new(Comparer<int>.Default);

    [Fact]
    public void Empty_heap_count_is_zero()
    {
        CreateHeap().Count.Should().Be(0);
    }

    [Fact]
    public void Extract_from_empty_throws()
    {
        var heap = CreateHeap();
        var act = () => heap.ExtractMin();
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Peek_from_empty_throws()
    {
        var heap = CreateHeap();
        var act = () => heap.Peek();
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Single_item()
    {
        var heap = CreateHeap();
        heap.Insert(42);
        heap.Count.Should().Be(1);
        heap.Peek().Should().Be(42);
        heap.ExtractMin().Should().Be(42);
        heap.Count.Should().Be(0);
    }

    [Fact]
    public void Extracts_in_sorted_order()
    {
        var heap = CreateHeap();
        int[] input = [5, 3, 8, 1, 9, 2, 7, 4, 6, 0];

        foreach (var item in input)
            heap.Insert(item);

        var result = new List<int>();
        while (heap.Count > 0)
            result.Add(heap.ExtractMin());

        result.Should().BeInAscendingOrder();
        result.Should().HaveCount(input.Length);
    }

    [Fact]
    public void Handles_duplicates()
    {
        var heap = CreateHeap();
        heap.Insert(3);
        heap.Insert(1);
        heap.Insert(3);
        heap.Insert(1);

        heap.ExtractMin().Should().Be(1);
        heap.ExtractMin().Should().Be(1);
        heap.ExtractMin().Should().Be(3);
        heap.ExtractMin().Should().Be(3);
    }

    [Fact]
    public void ReplaceMin_maintains_heap_property()
    {
        var heap = CreateHeap();
        heap.Insert(1);
        heap.Insert(5);
        heap.Insert(3);

        heap.Peek().Should().Be(1);
        heap.ReplaceMin(4); // replace 1 with 4
        heap.Peek().Should().Be(3);

        var result = new List<int>();
        while (heap.Count > 0)
            result.Add(heap.ExtractMin());

        result.Should().Equal(3, 4, 5);
    }

    [Fact]
    public void Large_random_dataset_extracts_sorted()
    {
        var heap = CreateHeap();
        var rng = new Random(123);
        int n = 10_000;

        for (int i = 0; i < n; i++)
            heap.Insert(rng.Next());

        var result = new List<int>(n);
        while (heap.Count > 0)
            result.Add(heap.ExtractMin());

        result.Should().BeInAscendingOrder();
        result.Should().HaveCount(n);
    }
}
