namespace Inventory.Application.Reporting.Shared;

/// <summary>
/// Retains at most <c>capacity</c> items from a sequence fed one at a time through <see cref="Add"/>,
/// always keeping the items that would sort earliest under the supplied <c>comparer</c>. Feeding every
/// item of a sequence and reading <see cref="Items"/> afterward yields the same ordered result as
/// sorting the complete sequence with the same comparer and taking its first <c>capacity</c> items,
/// without ever holding more than <c>capacity</c> items at once. Used to bound a paginated report's
/// retained row candidates to <c>page * pageSize</c> instead of materialising every matching row.
/// </summary>
public sealed class BoundedTopSelector<T>
{
    private readonly List<T> _items;
    private readonly IComparer<T> _comparer;
    private readonly int _capacity;

    public BoundedTopSelector(IComparer<T> comparer, int capacity)
    {
        _comparer = comparer;
        _capacity = Math.Max(capacity, 0);
        _items = new List<T>();
    }

    public IReadOnlyList<T> Items => _items;

    public void Add(T item)
    {
        if (_capacity == 0) return;

        if (_items.Count < _capacity)
        {
            Insert(item);
            return;
        }

        if (_comparer.Compare(item, _items[^1]) < 0)
        {
            _items.RemoveAt(_items.Count - 1);
            Insert(item);
        }
    }

    private void Insert(T item)
    {
        var index = _items.BinarySearch(item, _comparer);
        if (index < 0) index = ~index;
        _items.Insert(index, item);
    }
}
