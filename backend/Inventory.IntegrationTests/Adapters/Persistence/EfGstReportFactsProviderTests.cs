using Inventory.Domain.Gst;
using Inventory.Infrastructure.Reporting.Persistence;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Relational SQLite tests: the imported-summary query this provider reuses depends on SQL
/// translation and FK-backed navigation collections, not just in-memory LINQ semantics.
/// </summary>
public class EfGstReportFactsProviderTests
{
    private static async Task<SqliteConnection> CreateSqliteAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var setup = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await setup.Database.EnsureCreatedAsync();
        return connection;
    }

    [Fact]
    public async Task No_imported_reimbursements_in_range_reports_no_rows_and_no_gst_classification()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        var provider = new EfGstReportFactsProvider(db);

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 31), null, CancellationToken.None);

        Assert.False(facts.ImportedContainsRows);
        Assert.False(facts.ImportedContainsGstClassification);
    }

    [Fact]
    public async Task Imported_reimbursement_with_a_vat_classified_fee_reports_rows_and_gst_classification()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        var file = new ImportedFile { FileName = "aug.xml", FileHash = "aug", ImportedAt = DateTime.UtcNow };
        file.Reimbursements.Add(new ImportedReimbursement
        {
            ReimbursementStartDate = new DateTime(2025, 8, 1),
            ReimbursementEndDate = new DateTime(2025, 8, 31),
            Total = 110m,
            Fees = { new ImportedFee { FeeTypeDescription = "Processing fee", TotalSum = 10m, TotalSumWithVat = 11m, VatPercentage = 10m } }
        });
        db.ImportedFiles.Add(file);
        await db.SaveChangesAsync();
        var provider = new EfGstReportFactsProvider(db);

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 31), null, CancellationToken.None);

        Assert.True(facts.ImportedContainsRows);
        Assert.True(facts.ImportedContainsGstClassification);
    }

    [Fact]
    public async Task Imported_reimbursement_without_a_vat_percentage_reports_rows_but_no_gst_classification()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        var file = new ImportedFile { FileName = "aug.xml", FileHash = "aug", ImportedAt = DateTime.UtcNow };
        file.Reimbursements.Add(new ImportedReimbursement
        {
            ReimbursementStartDate = new DateTime(2025, 8, 1),
            ReimbursementEndDate = new DateTime(2025, 8, 31),
            Total = 110m,
            Fees = { new ImportedFee { FeeTypeDescription = "Processing fee", TotalSum = 10m, TotalSumWithVat = null, VatPercentage = null } }
        });
        db.ImportedFiles.Add(file);
        await db.SaveChangesAsync();
        var provider = new EfGstReportFactsProvider(db);

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 31), null, CancellationToken.None);

        Assert.True(facts.ImportedContainsRows);
        Assert.False(facts.ImportedContainsGstClassification);
    }

    [Fact]
    public async Task A_reimbursement_period_outside_the_requested_range_is_not_counted()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        var file = new ImportedFile { FileName = "jul.xml", FileHash = "jul", ImportedAt = DateTime.UtcNow };
        file.Reimbursements.Add(new ImportedReimbursement
        {
            ReimbursementStartDate = new DateTime(2025, 7, 1),
            ReimbursementEndDate = new DateTime(2025, 7, 31),
            Total = 50m,
            Fees = { new ImportedFee { FeeTypeDescription = "Processing fee", TotalSum = 5m, TotalSumWithVat = 5.5m, VatPercentage = 10m } }
        });
        db.ImportedFiles.Add(file);
        await db.SaveChangesAsync();
        var provider = new EfGstReportFactsProvider(db);

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 31), null, CancellationToken.None);

        Assert.False(facts.ImportedContainsRows);
        Assert.False(facts.ImportedContainsGstClassification);
    }
}

/// <summary>
/// Relational SQLite tests for the purchase GST components the GST accounting aid reads
/// (issue #432). They prove the real EF projection over the <c>Receipts</c>/<c>ReceiptItems</c>
/// tables: which purchase dates fall inside the report's inclusive range, that the stored per-line
/// and per-charge classifications are carried through untouched, that an absent charge stays
/// absent, and that the tenant query filters keep one business's purchases out of another's report.
/// </summary>
public class EfGstReportFactsProviderPurchaseTests
{
    private static readonly DateTime From = new(2026, 3, 1);
    private static readonly DateTime To = new(2026, 3, 31);

    private static async Task<SqliteConnection> CreateSqliteAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var setup = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await setup.Database.EnsureCreatedAsync();
        return connection;
    }

    private static AppDbContext Unrestricted(SqliteConnection connection) =>
        TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);

    private static Purchase TaxableLinePurchase(int id, DateTime purchaseDate, int businessId = 0) => new()
    {
        Id = id,
        Title = $"Order {id}",
        BusinessId = businessId,
        PurchaseDate = purchaseDate,
        Items =
        {
            new PurchaseItem
            {
                Id = id * 10,
                ProductId = 1,
                Quantity = 2m,
                UnitCost = 5.50m,
                BusinessId = businessId,
                GstClassification = GstClassification.Taxable,
                GstClassificationSource = GstClassificationSource.Manual
            }
        }
    };

    [Fact]
    public async Task Projects_each_line_and_present_charge_with_its_stored_classification()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = Unrestricted(connection);
        db.Products.Add(new Product { Id = 1, Name = "Coke" });
        var purchase = TaxableLinePurchase(1, new DateTime(2026, 3, 10));
        purchase.Items.Add(new PurchaseItem
        {
            Id = 11,
            ProductId = 1,
            Quantity = 1m,
            UnitCost = 7.30m,
            GstClassification = GstClassification.Unknown
        });
        purchase.DeliveryCost = 11m;
        purchase.DeliveryGstClassification = GstClassification.Taxable;
        purchase.PackageCost = 2.20m;
        purchase.PackageGstClassification = GstClassification.GstFree;
        db.Receipts.Add(purchase);
        await db.SaveChangesAsync();

        var facts = await new EfGstReportFactsProvider(db).GetFactsAsync(From, To, null, CancellationToken.None);

        var components = Assert.Single(facts.Purchases);
        Assert.Collection(components.Lines.OrderBy(line => line.UnitCost),
            line =>
            {
                Assert.Equal(2m, line.Quantity);
                Assert.Equal(5.50m, line.UnitCost);
                Assert.Equal(GstClassification.Taxable, line.Classification);
            },
            line =>
            {
                Assert.Equal(7.30m, line.UnitCost);
                Assert.Equal(GstClassification.Unknown, line.Classification);
            });
        Assert.Equal(11m, components.DeliveryCharge.Amount);
        Assert.Equal(GstClassification.Taxable, components.DeliveryCharge.Classification);
        Assert.Equal(2.20m, components.PackageCharge.Amount);
        Assert.Equal(GstClassification.GstFree, components.PackageCharge.Classification);
    }

    [Fact]
    public async Task A_null_or_zero_charge_is_projected_as_an_absent_charge()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = Unrestricted(connection);
        db.Products.Add(new Product { Id = 1, Name = "Coke" });
        var purchase = TaxableLinePurchase(1, new DateTime(2026, 3, 10));
        purchase.DeliveryCost = null;
        purchase.PackageCost = 0m;
        db.Receipts.Add(purchase);
        await db.SaveChangesAsync();

        var facts = await new EfGstReportFactsProvider(db).GetFactsAsync(From, To, null, CancellationToken.None);

        var components = Assert.Single(facts.Purchases);
        Assert.Null(components.DeliveryCharge.Amount);
        Assert.Equal(0m, components.PackageCharge.Amount);
    }

    [Theory]
    [InlineData(2026, 2, 28, false)]
    [InlineData(2026, 3, 1, true)]
    [InlineData(2026, 3, 31, true)]
    [InlineData(2026, 4, 1, false)]
    public async Task Only_purchases_dated_inside_the_reports_inclusive_range_are_returned(int year, int month, int day, bool expected)
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = Unrestricted(connection);
        db.Products.Add(new Product { Id = 1, Name = "Coke" });
        db.Receipts.Add(TaxableLinePurchase(1, new DateTime(year, month, day)));
        await db.SaveChangesAsync();

        var facts = await new EfGstReportFactsProvider(db).GetFactsAsync(From, To, null, CancellationToken.None);

        Assert.Equal(expected, facts.Purchases.Count == 1);
    }

    [Fact]
    public async Task A_purchase_late_on_the_last_day_of_the_range_is_still_inside_it()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = Unrestricted(connection);
        db.Products.Add(new Product { Id = 1, Name = "Coke" });
        db.Receipts.Add(TaxableLinePurchase(1, new DateTime(2026, 3, 31, 23, 59, 59)));
        await db.SaveChangesAsync();

        var facts = await new EfGstReportFactsProvider(db).GetFactsAsync(From, To, null, CancellationToken.None);

        Assert.Single(facts.Purchases);
    }

    [Fact]
    public async Task A_machine_filtered_request_returns_no_purchases_because_they_are_not_machine_attributable()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = Unrestricted(connection);
        db.Products.Add(new Product { Id = 1, Name = "Coke" });
        db.Receipts.Add(TaxableLinePurchase(1, new DateTime(2026, 3, 10)));
        await db.SaveChangesAsync();

        var facts = await new EfGstReportFactsProvider(db).GetFactsAsync(From, To, machineId: 7, CancellationToken.None);

        Assert.Empty(facts.Purchases);
    }

    [Fact]
    public async Task A_business_scoped_context_never_sees_another_businesss_purchase_components()
    {
        await using var connection = await CreateSqliteAsync();
        await using (var setup = Unrestricted(connection))
        {
            setup.Products.AddRange(
                new Product { Id = 1, Name = "Business A product", BusinessId = 1 },
                new Product { Id = 2, Name = "Business B product", BusinessId = 2 });
            var businessA = TaxableLinePurchase(1, new DateTime(2026, 3, 10), businessId: 1);
            var businessB = TaxableLinePurchase(2, new DateTime(2026, 3, 11), businessId: 2);
            businessB.Items.Single().ProductId = 2;
            businessB.Items.Single().UnitCost = 999m;
            setup.Receipts.AddRange(businessA, businessB);
            await setup.SaveChangesAsync();
        }

        await using var scopedDb = TestAppDbContext.For(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options, businessId: 1);

        var facts = await new EfGstReportFactsProvider(scopedDb).GetFactsAsync(From, To, null, CancellationToken.None);

        var components = Assert.Single(facts.Purchases);
        Assert.Equal(5.50m, Assert.Single(components.Lines).UnitCost);
    }

    [Fact]
    public async Task A_period_with_no_purchases_returns_an_empty_component_list_not_null()
    {
        await using var connection = await CreateSqliteAsync();
        await using var db = Unrestricted(connection);

        var facts = await new EfGstReportFactsProvider(db).GetFactsAsync(From, To, null, CancellationToken.None);

        Assert.Empty(facts.Purchases);
    }
}
