using Inventory.Application.PlatformDiagnostics;
using Xunit;

namespace InventoryApi.Tests.Application.PlatformDiagnostics;

/// <summary>
/// The permitted data surface as a contract (issue #336): exactly the seven table/column sets the
/// issue authorises, nothing wider, and fail-closed for everything else.
/// </summary>
public class PlatformDiagnosticsDataSurfaceTests
{
    /// <summary>
    /// Pinned literally, because this list is the authority a reviewer checks against the issue and
    /// a silent addition to it is the change that would matter most. The names are the physical
    /// ones: <c>Purchase</c> is mapped to <c>Receipts</c> and <c>PurchaseItem</c> to
    /// <c>ReceiptItems</c>.
    /// </summary>
    [Fact]
    public void The_surface_is_exactly_the_seven_authorised_tables_and_their_listed_columns()
    {
        var surface = PlatformDiagnosticsDataSurface.Tables
            .Select(table => $"{table.Table}({string.Join(", ", table.Columns)})")
            .ToArray();

        Assert.Equal(
            [
                "Businesses(Id)",
                "Categories(Id, BusinessId)",
                "Suppliers(Id, BusinessId)",
                "Products(Id, BusinessId, CategoryId, SupplierId)",
                "Receipts(Id, BusinessId, SupplierId)",
                "ReceiptItems(Id, BusinessId, ReceiptId, ProductId)",
                "StockAdjustments(Id, BusinessId, ProductId, ReceiptItemId)",
            ],
            surface);
    }

    [Theory]
    [InlineData("Products", "Id")]
    [InlineData("products", "businessid")]
    [InlineData("PRODUCTS", "CATEGORYID")]
    [InlineData("StockAdjustments", "ReceiptItemId")]
    public void A_listed_pair_is_permitted_case_insensitively_as_SQLite_compares_identifiers(
        string table,
        string column)
    {
        Assert.True(PlatformDiagnosticsDataSurface.Permits(table, column));
    }

    /// <summary>
    /// The columns that must stay unreachable: identity data, trading names, money, quantities,
    /// free text and the catalogue's own cost fields. Each of these lives on a table that <em>is</em>
    /// on the surface, which is the whole reason the allow-list is by pair and not by table.
    /// </summary>
    [Theory]
    [InlineData("Products", "Name")]
    [InlineData("Products", "UnitPrice")]
    [InlineData("Products", "AverageUnitCost")]
    [InlineData("Products", "InventoryValue")]
    [InlineData("Products", "Sku")]
    [InlineData("Products", "Description")]
    [InlineData("Businesses", "Name")]
    [InlineData("Businesses", "IsActive")]
    [InlineData("Suppliers", "Email")]
    [InlineData("Suppliers", "Phone")]
    [InlineData("Receipts", "StoredFileName")]
    [InlineData("Receipts", "TotalAmount")]
    [InlineData("ReceiptItems", "UnitCost")]
    [InlineData("StockAdjustments", "Notes")]
    [InlineData("StockAdjustments", "UnitCost")]
    public void An_unlisted_column_on_a_listed_table_is_denied(string table, string column)
    {
        Assert.True(PlatformDiagnosticsDataSurface.PermitsTable(table));
        Assert.False(PlatformDiagnosticsDataSurface.Permits(table, column));
    }

    /// <summary>
    /// The tables that must stay invisible entirely: Entra identity rows, raw imported Nayax
    /// payloads - where issue #327's token configuration would land - and SQLite's own schema.
    /// </summary>
    [Theory]
    [InlineData("BusinessMemberships")]
    [InlineData("BusinessBackfillAudits")]
    [InlineData("NayaxSales")]
    [InlineData("NayaxMachineStockEvents")]
    [InlineData("NayaxProcessingFeeRates")]
    [InlineData("ImportedFiles")]
    [InlineData("ImportedReimbursements")]
    [InlineData("ImportedFees")]
    [InlineData("OperatingExpenses")]
    [InlineData("SiteCommissionAgreements")]
    [InlineData("CommissionPayments")]
    [InlineData("InventoryCostRepairs")]
    [InlineData("SupplierOrders")]
    [InlineData("sqlite_master")]
    [InlineData("sqlite_sequence")]
    public void An_unlisted_table_is_denied_entirely(string table)
    {
        Assert.False(PlatformDiagnosticsDataSurface.PermitsTable(table));
        Assert.False(PlatformDiagnosticsDataSurface.Permits(table, "Id"));
    }

    /// <summary>
    /// The fail-closed default a future schema change relies on: a column nobody has reviewed is
    /// denied without this file or the enforcement adapter changing.
    /// </summary>
    [Fact]
    public void A_column_that_does_not_exist_yet_is_denied_without_any_change_here()
    {
        Assert.False(PlatformDiagnosticsDataSurface.Permits("Products", "NayaxAccessTokenCache"));
        Assert.False(PlatformDiagnosticsDataSurface.Permits("Businesses", "NayaxOperatorToken"));
    }

    [Fact]
    public void A_blank_or_missing_name_is_denied_rather_than_matching_anything()
    {
        Assert.False(PlatformDiagnosticsDataSurface.PermitsTable(null));
        Assert.False(PlatformDiagnosticsDataSurface.PermitsTable(string.Empty));
        Assert.False(PlatformDiagnosticsDataSurface.Permits("Products", null));
        Assert.False(PlatformDiagnosticsDataSurface.Permits("Products", string.Empty));
        Assert.False(PlatformDiagnosticsDataSurface.Permits(null, "Id"));
    }

    [Fact]
    public void No_table_exposes_a_wildcard_column()
    {
        Assert.All(
            PlatformDiagnosticsDataSurface.Tables,
            table => Assert.DoesNotContain("*", table.Columns));
    }
}
