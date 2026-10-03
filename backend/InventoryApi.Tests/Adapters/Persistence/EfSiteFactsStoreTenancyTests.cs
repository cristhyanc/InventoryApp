using InventoryApi.Adapters.Persistence;
using Inventory.Domain.FinancialConfiguration;
using InventoryApi.Data;
using InventoryApi.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Cross-tenant regression test for the migrated site dashboard slice (issue #241): the new
/// <see cref="EfSiteFactsStore"/> reads through the same <c>AppDbContext</c> global query filters
/// every other tenant-owned read uses, so a second business's catalogue, commission agreement, and
/// fee rate never leak into a caller scoped to the first business.
/// </summary>
public class EfSiteFactsStoreTenancyTests : IDisposable
{
    private const int BusinessA = 1;
    private const int BusinessB = 2;

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public EfSiteFactsStoreTenancyTests()
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

    [Fact]
    public async Task Product_cost_basis_excludes_the_other_business_products()
    {
        using (var seed = TestAppDbContext.Unrestricted(_options))
        {
            seed.Products.Add(new Product { BusinessId = BusinessA, Name = "A-Product", AverageUnitCost = 2m });
            seed.Products.Add(new Product { BusinessId = BusinessB, Name = "B-Product", AverageUnitCost = 9m });
            await seed.SaveChangesAsync();
        }

        using var db = TestAppDbContext.For(_options, BusinessA);
        var store = new EfSiteFactsStore(db);

        var costBasis = await store.GetProductCostBasisAsync(CancellationToken.None);

        Assert.Single(costBasis);
        Assert.Equal("A-Product", Assert.Single(costBasis.Values).Name);
    }

    /// <summary>
    /// The site dashboard now loads every site's recent completed sales with one batched read over the
    /// whole fleet's machine ids instead of one overlapping read per site (issue #313). A Nayax machine
    /// id is an external identity another business can legitimately hold rows for, so the batched read
    /// must still return only the caller's business's sales, through the same central query filters the
    /// former per-site reads used.
    /// </summary>
    [Fact]
    public async Task Batched_recent_completed_sales_read_excludes_the_other_business_sales()
    {
        var authorizationTime = DateTime.Now.AddDays(-1);
        using (var seed = TestAppDbContext.Unrestricted(_options))
        {
            seed.NayaxSales.AddRange(
                new NayaxSales { BusinessId = BusinessA, TransactionID = 1, MachineID = 10, SettlementValue = 5m, TransactionStatusId = NayaxTransactionStatusIds.Completed, MachineAuthorizationTime = authorizationTime },
                new NayaxSales { BusinessId = BusinessA, TransactionID = 2, MachineID = 20, SettlementValue = 7m, TransactionStatusId = NayaxTransactionStatusIds.Completed, MachineAuthorizationTime = authorizationTime },
                // The same machine ids on purpose: business B's rows for them must stay invisible.
                new NayaxSales { BusinessId = BusinessB, TransactionID = 1, MachineID = 10, SettlementValue = 900m, TransactionStatusId = NayaxTransactionStatusIds.Completed, MachineAuthorizationTime = authorizationTime },
                new NayaxSales { BusinessId = BusinessB, TransactionID = 2, MachineID = 20, SettlementValue = 900m, TransactionStatusId = NayaxTransactionStatusIds.Completed, MachineAuthorizationTime = authorizationTime });
            await seed.SaveChangesAsync();
        }

        using var db = TestAppDbContext.For(_options, BusinessA);
        var store = new EfSiteFactsStore(db);

        var sales = await store.GetRecentCompletedSalesAsync([10, 20], DateTime.Now.AddDays(-16), CancellationToken.None);

        Assert.Equal(12m, sales.Sum(sale => sale.SettlementValue));
        Assert.Equal(new long[] { 10, 20 }, sales.Select(sale => sale.MachineId).Order());
    }

    [Fact]
    public async Task Card_commission_resolution_ignores_the_other_business_agreement()
    {
        using (var seed = TestAppDbContext.Unrestricted(_options))
        {
            // Same SiteId on purpose: site identity is scoped by business, so this must not
            // collide with business A's own (absent) agreement for that ID.
            seed.SiteCommissionAgreements.Add(new SiteCommissionAgreement
            {
                BusinessId = BusinessB,
                SiteId = 42,
                EffectiveFrom = new DateTime(2020, 1, 1),
                CommissionRate = .50m,
                Basis = CommissionBasis.GrossSales,
            });
            await seed.SaveChangesAsync();
        }

        using var db = TestAppDbContext.For(_options, BusinessA);
        var store = new EfSiteFactsStore(db);

        var resolution = await store.ResolveCardCommissionAsync(42, DateTime.Today, [10m], CancellationToken.None);

        // Business A has zero agreements for site 42 (business B's is invisible), which is the
        // valid zero-commission case, not an unavailable configuration.
        Assert.False(resolution.ConfigurationUnavailable);
    }

    [Fact]
    public async Task Effective_fee_lookup_ignores_the_other_business_rate()
    {
        using (var seed = TestAppDbContext.Unrestricted(_options))
        {
            seed.NayaxProcessingFeeRates.Add(new NayaxProcessingFeeRate
            {
                BusinessId = BusinessB,
                EffectiveFrom = new DateTime(2020, 1, 1),
                FeeExGst = .25m,
            });
            await seed.SaveChangesAsync();
        }

        using var db = TestAppDbContext.For(_options, BusinessA);
        var store = new EfSiteFactsStore(db);

        var fee = await store.ResolveEffectiveFeeExGstAsync(DateTime.Today, CancellationToken.None);

        Assert.Null(fee);
    }

    [Fact]
    public async Task Commission_store_hides_other_business_agreements_for_same_site_id()
    {
        using (var seed = TestAppDbContext.Unrestricted(_options))
        {
            seed.SiteCommissionAgreements.Add(new SiteCommissionAgreement
            {
                BusinessId = BusinessB,
                SiteId = 42,
                EffectiveFrom = new DateTime(2020, 1, 1),
                CommissionRate = .50m,
                Basis = CommissionBasis.GrossSales,
            });
            await seed.SaveChangesAsync();
        }

        using var db = TestAppDbContext.For(_options, BusinessA);
        var store = new EfSiteCommissionStore(db);

        var agreements = await store.GetAgreementsAsync(42, CancellationToken.None);

        Assert.Empty(agreements);
    }
}
