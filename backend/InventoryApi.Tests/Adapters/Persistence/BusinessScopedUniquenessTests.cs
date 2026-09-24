using InventoryApi.Data;
using InventoryApi.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Uniqueness on tenant-owned data must be per business, not global (issue #64).
///
/// A globally unique constraint in a shared database is a cross-tenant defect even though it
/// leaks no data: whichever business writes a value first silently denies it to everyone else.
/// Every value exercised here is either a remote Nayax identifier or ordinary business
/// configuration, so two businesses holding the same one is normal and must be allowed - while
/// a genuine duplicate inside one business must still be refused.
///
/// These are relational SQLite tests because a unique index is a database behaviour.
/// </summary>
public class BusinessScopedUniquenessTests : IDisposable
{
    private const int BusinessA = 1;
    private const int BusinessB = 2;

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public BusinessScopedUniquenessTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var setup = TestAppDbContext.Unrestricted(_options);
        setup.Database.EnsureCreated();
        setup.Businesses.AddRange(
            new Business { Id = BusinessA, Name = "Vending A", CreatedAtUtc = DateTime.UtcNow },
            new Business { Id = BusinessB, Name = "Vending B", CreatedAtUtc = DateTime.UtcNow });
        setup.SaveChanges();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private void SaveAs(int businessId, Action<AppDbContext> write)
    {
        using var db = TestAppDbContext.For(_options, businessId);
        write(db);
        db.SaveChanges();
    }

    private static readonly DateTime EffectiveFrom = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

    #region The same value in two businesses is allowed

    /// <summary>
    /// Import de-duplication is per business. Two operators may legitimately receive and import
    /// the same Nayax file; the second must not be rejected as a duplicate of the first.
    /// </summary>
    [Fact]
    public void Two_businesses_can_import_a_file_with_the_same_hash()
    {
        const string SharedHash = "identical-file-hash";

        SaveAs(BusinessA, db => db.ImportedFiles.Add(new ImportedFile { FileName = "a.xml", FileHash = SharedHash }));
        SaveAs(BusinessB, db => db.ImportedFiles.Add(new ImportedFile { FileName = "b.xml", FileHash = SharedHash }));

        using var verify = TestAppDbContext.Unrestricted(_options);
        var files = verify.ImportedFiles.Where(f => f.FileHash == SharedHash).ToList();

        Assert.Equal(2, files.Count);
        Assert.Equal([BusinessA, BusinessB], files.Select(f => f.BusinessId).OrderBy(id => id));
    }

    [Fact]
    public void Two_businesses_can_configure_a_fee_rate_with_the_same_effective_date()
    {
        SaveAs(BusinessA, db => db.NayaxProcessingFeeRates.Add(
            new NayaxProcessingFeeRate { EffectiveFrom = EffectiveFrom, FeeExGst = 0.17m }));
        SaveAs(BusinessB, db => db.NayaxProcessingFeeRates.Add(
            new NayaxProcessingFeeRate { EffectiveFrom = EffectiveFrom, FeeExGst = 0.22m }));

        using var verify = TestAppDbContext.Unrestricted(_options);
        var rates = verify.NayaxProcessingFeeRates.Where(r => r.EffectiveFrom == EffectiveFrom).ToList();

        Assert.Equal(2, rates.Count);

        // Each business keeps its own rate: neither overwrote nor blocked the other.
        Assert.Equal(0.17m, rates.Single(r => r.BusinessId == BusinessA).FeeExGst);
        Assert.Equal(0.22m, rates.Single(r => r.BusinessId == BusinessB).FeeExGst);
    }

    /// <summary>
    /// SiteId is a remote Nayax identifier, so the same numeric site can exist in two operator
    /// accounts and mean two different places.
    /// </summary>
    [Fact]
    public void Two_businesses_can_hold_an_agreement_for_the_same_site_and_effective_date()
    {
        const long SharedSiteId = 4242;

        SaveAs(BusinessA, db => db.SiteCommissionAgreements.Add(
            new SiteCommissionAgreement { SiteId = SharedSiteId, EffectiveFrom = EffectiveFrom, CommissionRate = 0.10m }));
        SaveAs(BusinessB, db => db.SiteCommissionAgreements.Add(
            new SiteCommissionAgreement { SiteId = SharedSiteId, EffectiveFrom = EffectiveFrom, CommissionRate = 0.15m }));

        using var verify = TestAppDbContext.Unrestricted(_options);
        var agreements = verify.SiteCommissionAgreements
            .Where(a => a.SiteId == SharedSiteId && a.EffectiveFrom == EffectiveFrom)
            .ToList();

        Assert.Equal(2, agreements.Count);
        Assert.Equal(0.10m, agreements.Single(a => a.BusinessId == BusinessA).CommissionRate);
        Assert.Equal(0.15m, agreements.Single(a => a.BusinessId == BusinessB).CommissionRate);
    }

    /// <summary>
    /// The reason NayaxSales no longer uses TransactionID as its primary key: a remote id is
    /// unique only within the operator account that issued it.
    /// </summary>
    [Fact]
    public void Two_businesses_can_record_a_sale_with_the_same_nayax_transaction_id()
    {
        const long SharedTransactionId = 987654321;

        SaveAs(BusinessA, db => db.NayaxSales.Add(new NayaxSales
        {
            TransactionID = SharedTransactionId,
            MachineID = 11,
            SettlementValue = 3.50m,
            MachineAuthorizationTime = EffectiveFrom,
        }));
        SaveAs(BusinessB, db => db.NayaxSales.Add(new NayaxSales
        {
            TransactionID = SharedTransactionId,
            MachineID = 22,
            SettlementValue = 4.75m,
            MachineAuthorizationTime = EffectiveFrom,
        }));

        using var verify = TestAppDbContext.Unrestricted(_options);
        var sales = verify.NayaxSales.Where(s => s.TransactionID == SharedTransactionId).ToList();

        Assert.Equal(2, sales.Count);

        // Distinct local keys, and neither business's financial value was overwritten.
        Assert.Equal(2, sales.Select(s => s.Id).Distinct().Count());
        Assert.Equal(3.50m, sales.Single(s => s.BusinessId == BusinessA).SettlementValue);
        Assert.Equal(4.75m, sales.Single(s => s.BusinessId == BusinessB).SettlementValue);
    }

    [Fact]
    public void Each_business_sees_only_its_own_row_when_the_remote_transaction_id_is_shared()
    {
        const long SharedTransactionId = 555;

        SaveAs(BusinessA, db => db.NayaxSales.Add(new NayaxSales
        {
            TransactionID = SharedTransactionId,
            MachineID = 11,
            SettlementValue = 3.50m,
        }));
        SaveAs(BusinessB, db => db.NayaxSales.Add(new NayaxSales
        {
            TransactionID = SharedTransactionId,
            MachineID = 22,
            SettlementValue = 4.75m,
        }));

        using var a = TestAppDbContext.For(_options, BusinessA);
        var visible = Assert.Single(a.NayaxSales.Where(s => s.TransactionID == SharedTransactionId).ToList());

        Assert.Equal(3.50m, visible.SettlementValue);
        Assert.Equal(BusinessA, visible.BusinessId);
    }

    #endregion

    #region A duplicate inside one business is still refused

    [Fact]
    public void One_business_still_cannot_import_the_same_file_hash_twice()
    {
        SaveAs(BusinessA, db => db.ImportedFiles.Add(new ImportedFile { FileName = "a.xml", FileHash = "same-hash" }));

        var exception = Assert.Throws<DbUpdateException>(() =>
            SaveAs(BusinessA, db => db.ImportedFiles.Add(new ImportedFile { FileName = "again.xml", FileHash = "same-hash" })));

        Assert.IsType<SqliteException>(exception.InnerException);
    }

    [Fact]
    public void One_business_still_cannot_configure_two_fee_rates_for_the_same_effective_date()
    {
        SaveAs(BusinessA, db => db.NayaxProcessingFeeRates.Add(
            new NayaxProcessingFeeRate { EffectiveFrom = EffectiveFrom, FeeExGst = 0.17m }));

        Assert.Throws<DbUpdateException>(() =>
            SaveAs(BusinessA, db => db.NayaxProcessingFeeRates.Add(
                new NayaxProcessingFeeRate { EffectiveFrom = EffectiveFrom, FeeExGst = 0.19m })));
    }

    [Fact]
    public void One_business_still_cannot_hold_two_agreements_for_the_same_site_and_date()
    {
        SaveAs(BusinessA, db => db.SiteCommissionAgreements.Add(
            new SiteCommissionAgreement { SiteId = 7, EffectiveFrom = EffectiveFrom, CommissionRate = 0.10m }));

        Assert.Throws<DbUpdateException>(() =>
            SaveAs(BusinessA, db => db.SiteCommissionAgreements.Add(
                new SiteCommissionAgreement { SiteId = 7, EffectiveFrom = EffectiveFrom, CommissionRate = 0.12m })));
    }

    [Fact]
    public void One_business_still_cannot_record_the_same_nayax_transaction_id_twice()
    {
        SaveAs(BusinessA, db => db.NayaxSales.Add(new NayaxSales { TransactionID = 42, MachineID = 1, SettlementValue = 1m }));

        Assert.Throws<DbUpdateException>(() =>
            SaveAs(BusinessA, db => db.NayaxSales.Add(new NayaxSales { TransactionID = 42, MachineID = 1, SettlementValue = 2m })));
    }

    [Fact]
    public void One_business_still_cannot_hold_two_cost_transition_baselines_for_one_product()
    {
        long productId = 0;
        SaveAs(BusinessA, db =>
        {
            var product = new Product { Name = "Chips", UnitPrice = 2m };
            db.Products.Add(product);
            db.SaveChanges();
            productId = product.Id;
            db.InventoryCostTransitionBaselines.Add(new InventoryCostTransitionBaseline { ProductId = productId });
        });

        Assert.Throws<DbUpdateException>(() =>
            SaveAs(BusinessA, db => db.InventoryCostTransitionBaselines.Add(
                new InventoryCostTransitionBaseline { ProductId = productId })));
    }

    #endregion
}
