namespace ExternalSorting.Core.Merge;

/// <summary>
/// Binary min-heap for k-way merge. O(log K) insert/extract.
/// </summary>
public sealed class MinHeap<T>
{
    private readonly IComparer<T> _comparer;
    private readonly List<T> _items;

    public MinHeap(IComparer<T> comparer, int capacity = 16)
    {
        _comparer = comparer;
        _items = new List<T>(capacity);
    }

    public int Count => _items.Count;

    public void Insert(T item)
    {
        _items.Add(item);
        SiftUp(_items.Count - 1);
    }

    public T Peek()
    {
        if (_items.Count == 0) throw new InvalidOperationException("Heap is empty.");
        return _items[0];
    }

    public T ExtractMin()
    {
        if (_items.Count == 0) throw new InvalidOperationException("Heap is empty.");

        var min = _items[0];
        int last = _items.Count - 1;
        _items[0] = _items[last];
        _items.RemoveAt(last);

        if (_items.Count > 0)
            SiftDown(0);

        return min;
    }

    /// <summary>Replace root with new item and re-heapify. Faster than Extract+Insert.</summary>
    public void ReplaceMin(T item)
    {
        if (_items.Count == 0) throw new InvalidOperationException("Heap is empty.");
        _items[0] = item;
        SiftDown(0);
    }

    private void SiftUp(int i)
    {
        while (i > 0)
        {
            int parent = (i - 1) / 2;
            if (_comparer.Compare(_items[i], _items[parent]) >= 0)
                break;
            (_items[i], _items[parent]) = (_items[parent], _items[i]);
            i = parent;
        }
    }

    private void SiftDown(int i)
    {
        int count = _items.Count;
        while (true)
        {
            int smallest = i;
            int left = 2 * i + 1;
            int right = 2 * i + 2;

            if (left < count && _comparer.Compare(_items[left], _items[smallest]) < 0)
                smallest = left;
            if (right < count && _comparer.Compare(_items[right], _items[smallest]) < 0)
                smallest = right;

            if (smallest == i)
                break;

            (_items[i], _items[smallest]) = (_items[smallest], _items[i]);
            i = smallest;
        }
    }
}
