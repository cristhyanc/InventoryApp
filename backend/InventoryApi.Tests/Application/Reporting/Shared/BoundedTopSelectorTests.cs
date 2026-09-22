using System;
using System.Collections.Generic;
using System.Linq;
using Inventory.Application.Reporting.Shared;
using Xunit;

namespace InventoryApi.Tests.Application.Reporting.Shared;

/// <summary>
/// Focused tests for the bounded page-selection component the transaction sales report uses to
/// retain only the sorted row candidates a paginated request needs, instead of every matching row.
/// </summary>
public class BoundedTopSelectorTests
{
    [Fact]
    public void Never_retains_more_items_than_capacity_even_while_feeding_a_large_sequence()
    {
        var random = new Random(12345);
        var items = Enumerable.Range(0, 10_000).Select(_ => random.Next(0, 1_000_000)).ToList();
        var selector = new BoundedTopSelector<int>(Comparer<int>.Default, capacity: 37);

        foreach (var item in items)
        {
            selector.Add(item);
            Assert.True(selector.Items.Count <= 37);
        }

        Assert.Equal(37, selector.Items.Count);
    }

    [Fact]
    public void Matches_full_ascending_sort_and_take_for_a_large_input_sequence()
    {
        var random = new Random(2468);
        var items = Enumerable.Range(0, 5_000).Select(_ => random.Next(-500_000, 500_000)).ToList();
        var selector = new BoundedTopSelector<int>(Comparer<int>.Default, capacity: 250);

        foreach (var item in items) selector.Add(item);

        var expected = items.OrderBy(x => x).Take(250).ToList();
        Assert.Equal(expected, selector.Items);
    }

    [Fact]
    public void Matches_full_descending_sort_and_take_for_a_large_input_sequence()
    {
        var random = new Random(97531);
        var items = Enumerable.Range(0, 5_000).Select(_ => random.Next(-500_000, 500_000)).ToList();
        var descending = Comparer<int>.Create((a, b) => b.CompareTo(a));
        var selector = new BoundedTopSelector<int>(descending, capacity: 250);

        foreach (var item in items) selector.Add(item);

        var expected = items.OrderByDescending(x => x).Take(250).ToList();
        Assert.Equal(expected, selector.Items);
    }

    [Fact]
    public void Retains_every_item_in_non_decreasing_key_order_when_many_items_share_a_key()
    {
        var items = Enumerable.Range(0, 400).Select(i => i % 5).ToList();
        var selector = new BoundedTopSelector<int>(Comparer<int>.Default, capacity: 400);

        foreach (var item in items) selector.Add(item);

        Assert.Equal(400, selector.Items.Count);
        Assert.Equal(items.OrderBy(x => x), selector.Items);
        Assert.True(selector.Items.SequenceEqual(selector.Items.OrderBy(x => x)));
    }

    [Fact]
    public void Capacity_larger_than_input_retains_every_item_fully_sorted()
    {
        var items = new[] { 5, 3, 8, 1, 9, 2 };
        var selector = new BoundedTopSelector<int>(Comparer<int>.Default, capacity: 100);

        foreach (var item in items) selector.Add(item);

        Assert.Equal(items.OrderBy(x => x), selector.Items);
    }

    [Fact]
    public void Zero_capacity_retains_nothing()
    {
        var selector = new BoundedTopSelector<int>(Comparer<int>.Default, capacity: 0);

        foreach (var item in new[] { 1, 2, 3 }) selector.Add(item);

        Assert.Empty(selector.Items);
    }
}
