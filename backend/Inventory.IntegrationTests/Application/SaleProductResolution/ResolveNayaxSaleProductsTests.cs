using Inventory.Application.SaleProductResolution;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.Providers;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using InventoryApi.Tests.Application.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Application.SaleProductResolution;

/// <summary>
/// The audited, idempotent operation that settles an unresolved sale's local product (issue #758),
/// over relational SQLite and the real EF adapter.
///
/// <para>It is the only thing in the application that may resolve a <strong>conflicting</strong>
/// sale - one whose stored number named one product directly and a different one through a
/// recorded provider id - and it does so only because a human named the product. Everything it
/// writes is an audit row carrying the old value, the new value, the rule, the candidates, who
/// decided and when.</para>
/// </summary>
public class ResolveNayaxSaleProductsTests : IDisposable
{
    private const int BusinessA = 1;
    private const int BusinessB = 2;
    private const long DirectCandidate = 777;
    private const long ExternalCandidate = 7100;

    private static readonly DateTime DecidedAt = new(2026, 10, 18, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SoldAt = new(2026, 10, 3, 19, 30, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public ResolveNayaxSaleProductsTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var setup = TestAppDbContext.Unrestricted(_options);
        setup.Database.EnsureCreated();
        setup.Businesses.AddRange(NayaxBusiness(BusinessA, "Vending A"), NayaxBusiness(BusinessB, "Vending B"));
        setup.SaveChanges();

        using var seed = TestAppDbContext.For(_options, BusinessA);
        seed.Products.AddRange(
            new Product { Id = DirectCandidate, Name = "Unrelated snack", QuantityInStock = 5 },
            new Product
            {
                Id = ExternalCandidate,
                Name = "Imported bar",
                ExternalProductId = NayaxExternalId.From(DirectCandidate),
                QuantityInStock = 5,
            });
        seed.SaveChanges();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task A_dry_run_reports_the_conflict_and_its_candidates_and_writes_nothing()
    {
        var saleId = await SeedConflictingSaleAsync(costed: true);

        var report = await RunAsync([], dryRun: true);

        var sale = Assert.Single(report.Unresolved);
        Assert.Equal(saleId, sale.SaleId);
        Assert.Equal(DirectCandidate, sale.ConflictDirectCandidateProductId);
        Assert.Equal(ExternalCandidate, sale.ConflictExternalCandidateProductId);
        Assert.True(sale.IsCosted);
        Assert.Equal(1, report.ConflictingCount);
        Assert.Equal(1, report.UnresolvedCostedCount);

        await using var db = Context(BusinessA);
        Assert.Null((await db.NayaxSales.AsNoTracking().SingleAsync()).ProductId);
        Assert.Empty(await db.NayaxSaleProductResolutions.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task A_decision_resolves_the_sale_and_records_the_candidates_who_decided_and_when()
    {
        var saleId = await SeedConflictingSaleAsync(costed: true);

        var report = await RunAsync([new NayaxSaleProductDecision(saleId, ExternalCandidate)], dryRun: false);

        Assert.Empty(report.Refusals);
        Assert.Empty(report.Unresolved);
        Assert.Equal(ExternalCandidate, Assert.Single(report.Applied).ProductId);

        await using var db = Context(BusinessA);
        Assert.Equal(ExternalCandidate, (await db.NayaxSales.AsNoTracking().SingleAsync()).ProductId);

        var audit = await db.NayaxSaleProductResolutions.AsNoTracking().SingleAsync();
        Assert.Equal(saleId, audit.NayaxSaleId);
        Assert.Equal(5050, audit.TransactionId);
        Assert.Null(audit.PreviousProductId);
        Assert.Equal(ExternalCandidate, audit.NewProductId);
        Assert.Equal(NayaxSaleProductResolutionRule.OperatorDecision, audit.Rule);
        Assert.Equal("777,7100", audit.CandidateProductIds);
        Assert.Equal("A. Operator", audit.DecidedBy);
        Assert.Equal(DecidedAt, audit.ResolvedAtUtc);
    }

    /// <summary>
    /// Rerunning the same decision writes nothing at all: the sale is no longer unresolved, so
    /// there is nothing to fill, and the attribution it already carries is never moved.
    /// </summary>
    [Fact]
    public async Task Rerunning_an_applied_decision_changes_nothing_and_writes_no_second_audit_row()
    {
        var saleId = await SeedConflictingSaleAsync(costed: true);
        var decisions = new[] { new NayaxSaleProductDecision(saleId, ExternalCandidate) };

        await RunAsync(decisions, dryRun: false);
        var second = await RunAsync(decisions, dryRun: false);

        Assert.Empty(second.Applied);
        await using var db = Context(BusinessA);
        Assert.Equal(ExternalCandidate, (await db.NayaxSales.AsNoTracking().SingleAsync()).ProductId);
        Assert.Single(await db.NayaxSaleProductResolutions.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// A decision naming a product the caller's business does not hold is refused before anything
    /// is staged, so a mistyped product id cannot become an attribution and cannot reach the
    /// foreign key as a save failure either.
    /// </summary>
    [Fact]
    public async Task A_decision_naming_another_businesss_product_is_refused_and_writes_nothing()
    {
        await using (var other = Context(BusinessB))
        {
            other.Products.Add(new Product { Id = 6001, Name = "Business B snack", QuantityInStock = 5 });
            await other.SaveChangesAsync();
        }

        var saleId = await SeedConflictingSaleAsync(costed: false);

        var report = await RunAsync([new NayaxSaleProductDecision(saleId, 6001)], dryRun: false);

        Assert.Contains(report.Refusals, refusal => refusal.Contains("not a product of this business", StringComparison.Ordinal));
        await using var db = Context(BusinessA);
        Assert.Null((await db.NayaxSales.AsNoTracking().SingleAsync()).ProductId);
        Assert.Empty(await db.NayaxSaleProductResolutions.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// A dry run with a decision reports what it would do and still writes nothing, so an apply can
    /// be gated on a clean dry run.
    /// </summary>
    [Fact]
    public async Task A_dry_run_with_a_decision_reports_the_plan_and_still_writes_nothing()
    {
        var saleId = await SeedConflictingSaleAsync(costed: false);

        var report = await RunAsync([new NayaxSaleProductDecision(saleId, DirectCandidate)], dryRun: true);

        Assert.Equal(DirectCandidate, Assert.Single(report.Applied).ProductId);
        await using var db = Context(BusinessA);
        Assert.Null((await db.NayaxSales.AsNoTracking().SingleAsync()).ProductId);
        Assert.Empty(await db.NayaxSaleProductResolutions.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// A sale that is merely unresolved - no conflict recorded, because nothing was ambiguous, just
    /// unmatched - is resolvable the same way, with no candidates to record.
    /// </summary>
    [Fact]
    public async Task An_unresolved_sale_with_no_recorded_conflict_is_resolvable_with_no_candidates()
    {
        long saleId;
        await using (var seed = Context(BusinessA))
        {
            var sale = Sale(5051, costed: false);
            seed.NayaxSales.Add(sale);
            await seed.SaveChangesAsync();
            saleId = sale.Id;
        }

        await RunAsync([new NayaxSaleProductDecision(saleId, DirectCandidate)], dryRun: false);

        await using var db = Context(BusinessA);
        Assert.Equal(DirectCandidate, (await db.NayaxSales.AsNoTracking().SingleAsync()).ProductId);
        Assert.Null((await db.NayaxSaleProductResolutions.AsNoTracking().SingleAsync()).CandidateProductIds);
    }

    private async Task<NayaxSaleProductResolutionReport> RunAsync(
        IReadOnlyCollection<NayaxSaleProductDecision> decisions, bool dryRun)
    {
        await using var db = Context(BusinessA);
        return await new ResolveNayaxSaleProducts(
                new EfNayaxSaleProductResolutionStore(db, new FakeClock(DecidedAt)))
            .Handle(decisions, "A. Operator", dryRun, CancellationToken.None);
    }

    private async Task<long> SeedConflictingSaleAsync(bool costed)
    {
        await using var db = Context(BusinessA);
        var sale = Sale(5050, costed);
        db.NayaxSales.Add(sale);
        await db.SaveChangesAsync();

        db.NayaxSaleProductConflicts.Add(new NayaxSaleProductConflict
        {
            NayaxSaleId = sale.Id,
            TransactionId = sale.TransactionID,
            DirectCandidateProductId = DirectCandidate,
            ExternalCandidateProductId = ExternalCandidate,
            DetectedAtUtc = DecidedAt.AddDays(-1),
        });
        await db.SaveChangesAsync();
        return sale.Id;
    }

    private static NayaxSales Sale(long transactionId, bool costed) => new()
    {
        TransactionID = transactionId,
        TransactionStatusId = NayaxTransactionStatusIds.Completed,
        MachineID = 7,
        ProductId = null,
        NayaxProductId = DirectCandidate,
        ProductName = "Imported bar",
        SettlementValue = 3.50m,
        MachineAuthorizationTime = SoldAt,
        UnitCostAtSale = costed ? 1m : null,
        CostOfGoodsSold = costed ? 1m : null,
        CostingStatus = costed ? SaleCostingStatus.Costed : SaleCostingStatus.Pending,
    };

    private AppDbContext Context(int businessId) => TestAppDbContext.For(_options, businessId);

    private static Business NayaxBusiness(int id, string name) => new()
    {
        Id = id,
        Name = name,
        Status = BusinessStatus.Active,
        SalesProvider = ExternalProvider.Nayax,
        CreatedAtUtc = DateTime.UtcNow,
    };
}
