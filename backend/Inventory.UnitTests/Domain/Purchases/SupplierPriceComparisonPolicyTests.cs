using Inventory.Domain.Purchases;
using Xunit;

namespace InventoryApi.Tests.Domain.Purchases;

public class SupplierPriceComparisonPolicyTests
{
    private static SupplierPriceHistoryEntry Entry(
        int purchaseItemId,
        int purchaseId,
        string purchaseTitle,
        DateTime purchaseDate,
        int? supplierId,
        string? supplierName,
        decimal unitCost) =>
        new(purchaseItemId, purchaseId, purchaseTitle, purchaseDate, supplierId, supplierName, unitCost);

    [Fact]
    public void No_history_reports_no_lowest_or_latest_and_an_empty_history()
    {
        var result = SupplierPriceComparisonPolicy.Evaluate(Array.Empty<SupplierPriceHistoryEntry>());

        Assert.Null(result.Lowest);
        Assert.Null(result.Latest);
        Assert.Null(result.AbsoluteDifference);
        Assert.Null(result.PercentageDifference);
        Assert.False(result.PercentageIsMeaningful);
        Assert.Empty(result.HistoryNewestFirst);
    }

    [Fact]
    public void Multiple_suppliers_and_prices_identify_the_lowest_and_the_latest_independently()
    {
        var entries = new[]
        {
            Entry(1, 10, "January order", new DateTime(2026, 1, 5), 1, "Acme Supplies", 2.00m),
            Entry(2, 11, "March order", new DateTime(2026, 3, 5), 2, "Best Wholesale", 1.50m),
            Entry(3, 12, "June order", new DateTime(2026, 6, 5), 1, "Acme Supplies", 1.80m)
        };

        var result = SupplierPriceComparisonPolicy.Evaluate(entries);

        Assert.Equal(2, result.Lowest!.Value.PurchaseItemId);
        Assert.Equal("Best Wholesale", result.Lowest.Value.SupplierName);
        Assert.Equal(1.50m, result.Lowest.Value.UnitCost);

        Assert.Equal(3, result.Latest!.Value.PurchaseItemId);
        Assert.Equal("Acme Supplies", result.Latest.Value.SupplierName);
        Assert.Equal(1.80m, result.Latest.Value.UnitCost);

        Assert.Equal(0.30m, result.AbsoluteDifference);
        Assert.True(result.PercentageIsMeaningful);
        Assert.Equal(20.00m, result.PercentageDifference);
    }

    [Fact]
    public void History_is_ordered_newest_first()
    {
        var entries = new[]
        {
            Entry(1, 10, "Oldest", new DateTime(2026, 1, 1), null, null, 1.00m),
            Entry(2, 11, "Newest", new DateTime(2026, 6, 1), null, null, 1.00m),
            Entry(3, 12, "Middle", new DateTime(2026, 3, 1), null, null, 1.00m)
        };

        var result = SupplierPriceComparisonPolicy.Evaluate(entries);

        Assert.Equal(new[] { 2, 3, 1 }, result.HistoryNewestFirst.Select(e => e.PurchaseItemId));
    }

    [Fact]
    public void Missing_supplier_remains_visible_as_a_null_source_rather_than_being_omitted()
    {
        var entries = new[]
        {
            Entry(1, 10, "No supplier recorded", new DateTime(2026, 1, 1), null, null, 3.00m)
        };

        var result = SupplierPriceComparisonPolicy.Evaluate(entries);

        Assert.Single(result.HistoryNewestFirst);
        Assert.Null(result.HistoryNewestFirst[0].SupplierId);
        Assert.Null(result.HistoryNewestFirst[0].SupplierName);
        Assert.Equal(1, result.Lowest!.Value.PurchaseItemId);
    }

    [Fact]
    public void Zero_lowest_cost_reports_the_absolute_difference_without_a_misleading_percentage()
    {
        var entries = new[]
        {
            Entry(1, 10, "Free promotional stock", new DateTime(2026, 1, 1), 1, "Acme", 0.00m),
            Entry(2, 11, "Regular order", new DateTime(2026, 2, 1), 1, "Acme", 2.00m)
        };

        var result = SupplierPriceComparisonPolicy.Evaluate(entries);

        Assert.Equal(0.00m, result.Lowest!.Value.UnitCost);
        Assert.Equal(2.00m, result.AbsoluteDifference);
        Assert.False(result.PercentageIsMeaningful);
        Assert.Null(result.PercentageDifference);
    }

    [Fact]
    public void Equal_lowest_costs_deterministically_pick_the_earliest_occurrence_without_discarding_the_tie()
    {
        var entries = new[]
        {
            Entry(1, 10, "Later at the same low price", new DateTime(2026, 3, 1), 2, "Best Wholesale", 1.00m),
            Entry(2, 11, "First at the low price", new DateTime(2026, 1, 1), 1, "Acme Supplies", 1.00m)
        };

        var result = SupplierPriceComparisonPolicy.Evaluate(entries);

        Assert.Equal(2, result.Lowest!.Value.PurchaseItemId);
        Assert.Equal(2, result.HistoryNewestFirst.Count);
    }

    [Fact]
    public void Tied_latest_dates_are_broken_deterministically_by_the_higher_purchase_item_id()
    {
        var entries = new[]
        {
            Entry(1, 10, "Entered first", new DateTime(2026, 5, 1), 1, "Acme Supplies", 2.00m),
            Entry(2, 11, "Entered second, same date", new DateTime(2026, 5, 1), 2, "Best Wholesale", 2.50m)
        };

        var result = SupplierPriceComparisonPolicy.Evaluate(entries);

        Assert.Equal(2, result.Latest!.Value.PurchaseItemId);
        Assert.Equal(new[] { 2, 1 }, result.HistoryNewestFirst.Select(e => e.PurchaseItemId));
    }
}
