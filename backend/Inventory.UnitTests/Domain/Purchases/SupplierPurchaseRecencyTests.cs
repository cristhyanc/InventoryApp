using Inventory.Domain.Purchases;
using Xunit;

namespace InventoryApi.Tests.Domain.Purchases;

/// <summary>
/// The one shared ordering rule for "the latest purchase" (issue #1165, extracted from
/// <see cref="SupplierPriceComparisonPolicy"/>'s issue #63 tie-break). These tests pin the ordering
/// itself, so the price comparison and the receipt-scan pack-size lookup - which both consume it -
/// cannot diverge.
/// </summary>
public class SupplierPurchaseRecencyTests
{
    private sealed record Line(int PurchaseItemId, DateTime PurchaseDate);

    private static IReadOnlyList<int> NewestFirstIds(params Line[] lines) =>
        SupplierPurchaseRecency
            .NewestFirst(lines, line => line.PurchaseDate, line => line.PurchaseItemId)
            .Select(line => line.PurchaseItemId)
            .ToList();

    [Fact]
    public void The_later_purchase_date_comes_first_whatever_order_the_lines_arrive_in()
    {
        var ids = NewestFirstIds(
            new Line(1, new DateTime(2026, 3, 5)),
            new Line(2, new DateTime(2026, 6, 5)),
            new Line(3, new DateTime(2026, 1, 5)));

        Assert.Equal(new[] { 2, 1, 3 }, ids);
    }

    [Fact]
    public void A_same_day_tie_breaks_to_the_higher_purchase_item_id()
    {
        var sameDay = new DateTime(2026, 6, 5);

        var ids = NewestFirstIds(
            new Line(7, sameDay),
            new Line(9, sameDay),
            new Line(8, sameDay));

        Assert.Equal(new[] { 9, 8, 7 }, ids);
    }

    [Fact]
    public void The_date_decides_before_the_purchase_item_id_does()
    {
        // A lower id on the later date still wins: the id is only the same-day tie-break, never a
        // substitute for the date (a backdated purchase recorded today is not the latest purchase).
        var ids = NewestFirstIds(
            new Line(99, new DateTime(2026, 1, 5)),
            new Line(1, new DateTime(2026, 6, 5)));

        Assert.Equal(new[] { 1, 99 }, ids);
    }

    [Fact]
    public void A_purchase_dates_time_of_day_is_part_of_the_ordering()
    {
        var ids = NewestFirstIds(
            new Line(1, new DateTime(2026, 6, 5, 18, 0, 0)),
            new Line(2, new DateTime(2026, 6, 5, 6, 0, 0)));

        Assert.Equal(new[] { 1, 2 }, ids);
    }

    [Fact]
    public void No_lines_order_to_nothing()
    {
        Assert.Empty(NewestFirstIds());
    }

    [Fact]
    public void The_price_comparison_policy_orders_its_history_with_this_same_rule()
    {
        var sameDay = new DateTime(2026, 6, 5);
        var entries = new[]
        {
            new SupplierPriceHistoryEntry(1, 10, "First of the day", sameDay, 1, "Acme Supplies", 2.00m),
            new SupplierPriceHistoryEntry(3, 12, "Last of the day", sameDay, 1, "Acme Supplies", 1.80m),
            new SupplierPriceHistoryEntry(2, 11, "Earlier order", new DateTime(2026, 1, 5), 1, "Acme Supplies", 1.50m)
        };

        var result = SupplierPriceComparisonPolicy.Evaluate(entries);

        var expected = SupplierPurchaseRecency
            .NewestFirst(entries, entry => entry.PurchaseDate, entry => entry.PurchaseItemId)
            .Select(entry => entry.PurchaseItemId)
            .ToList();
        Assert.Equal(expected, result.HistoryNewestFirst.Select(entry => entry.PurchaseItemId).ToList());
        Assert.Equal(3, result.Latest!.Value.PurchaseItemId);
    }
}
