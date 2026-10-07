using Inventory.Application.Machines;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Relational SQLite tests for the home Dashboard sales card's facts adapter (issue #459). They are
/// relational rather than InMemory because what is under test is the SQL: the two-period grouping,
/// the inclusive instant boundaries, the completed-sale predicate, and - most importantly - that the
/// <c>AppDbContext</c> tenant query filter is the only thing scoping these aggregates to a business.
/// </summary>
public class EfDashboardSummarySalesFactsProviderTests : IDisposable
{
    private const int BusinessA = 1;
    private const int BusinessB = 2;

    // A Sydney week: Monday 28 September 2026 14:00 UTC is Tuesday 29 September in Sydney, so these
    // instants are deliberately read as instants only; the Sydney-date behaviour is covered by
    // GetDashboardSummarySydneyPeriodTests.
    private static readonly DateTime CurrentStart = new(2026, 10, 4, 14, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime CurrentEnd = new(2026, 10, 7, 9, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime PriorStart = new(2026, 9, 27, 14, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime PriorEnd = new(2026, 9, 30, 9, 30, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public EfDashboardSummarySalesFactsProviderTests()
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

    private static MachineDashboardPeriodUtc Period(DateTime startUtc, DateTime endUtc) =>
        new(startUtc, endUtc, startUtc.Date, endUtc.Date);

    private void Seed(params NayaxSales[] sales)
    {
        using var setup = TestAppDbContext.Unrestricted(_options);
        setup.NayaxSales.AddRange(sales);
        setup.SaveChanges();
    }

    private static NayaxSales Sale(
        int businessId, long transactionId, DateTime authorizedUtc, decimal settlementValue) =>
        SaleWithStatus(
            businessId, transactionId, authorizedUtc, settlementValue, NayaxTransactionStatusIds.Completed);

    private static NayaxSales SaleWithStatus(
        int businessId, long transactionId, DateTime authorizedUtc, decimal settlementValue, int? statusId) =>
        new()
        {
            BusinessId = businessId,
            TransactionID = transactionId,
            MachineID = 10,
            SettlementValue = settlementValue,
            PaymentMethod = "Credit",
            MachineAuthorizationTime = authorizedUtc,
            TransactionStatusId = statusId,
        };

    private async Task<Inventory.Application.Dashboard.DashboardSummarySalesFacts> FactsFor(int businessId)
    {
        await using var db = TestAppDbContext.For(_options, businessId);
        return await new EfDashboardSummarySalesFactsProvider(db).GetSalesFactsAsync(
            Period(CurrentStart, CurrentEnd), Period(PriorStart, PriorEnd), CancellationToken.None);
    }

    [Fact]
    public async Task Each_period_is_totalled_separately_and_no_sale_is_counted_twice()
    {
        Seed(
            Sale(BusinessA, 1, CurrentStart.AddHours(1), 10m),
            Sale(BusinessA, 2, CurrentEnd.AddMinutes(-5), 15m),
            Sale(BusinessA, 3, PriorStart.AddHours(1), 7m));

        var facts = await FactsFor(BusinessA);

        Assert.Equal(25m, facts.CurrentPeriodSales);
        Assert.Equal(2, facts.CurrentPeriodTransactionCount);
        Assert.Equal(7m, facts.PriorPeriodSales);
        Assert.Equal(1, facts.PriorPeriodTransactionCount);
    }

    /// <summary>
    /// Both bounds are inclusive, the convention the dashboard periods are expressed in; a sale one
    /// millisecond outside either end belongs to neither period.
    /// </summary>
    [Fact]
    public async Task Both_period_bounds_are_inclusive()
    {
        Seed(
            Sale(BusinessA, 1, CurrentStart, 1m),
            Sale(BusinessA, 2, CurrentEnd, 2m),
            Sale(BusinessA, 3, CurrentStart.AddMilliseconds(-1), 100m),
            Sale(BusinessA, 4, CurrentEnd.AddMilliseconds(1), 200m),
            Sale(BusinessA, 5, PriorStart, 4m),
            Sale(BusinessA, 6, PriorEnd, 8m));

        var facts = await FactsFor(BusinessA);

        Assert.Equal(3m, facts.CurrentPeriodSales);
        Assert.Equal(12m, facts.PriorPeriodSales);
    }

    /// <summary>
    /// Only status 12 is an approved sale. Pending, refunded, cancelled/declined and unknown rows
    /// must never reach a revenue figure, however visible they stay in data-quality reporting.
    /// </summary>
    [Theory]
    [InlineData(55)]
    [InlineData(80)]
    [InlineData(62)]
    [InlineData(26)]
    [InlineData(28)]
    [InlineData(31)]
    [InlineData(250)]
    [InlineData(null)]
    public async Task Only_completed_sales_contribute_revenue(int? statusId)
    {
        Seed(
            Sale(BusinessA, 1, CurrentStart.AddHours(1), 10m),
            SaleWithStatus(BusinessA, 2, CurrentStart.AddHours(2), 999m, statusId));

        var facts = await FactsFor(BusinessA);

        Assert.Equal(10m, facts.CurrentPeriodSales);
        Assert.Equal(1, facts.CurrentPeriodTransactionCount);
    }

    [Fact]
    public async Task The_earliest_recorded_completed_sale_is_reported_from_the_whole_history()
    {
        var earliest = PriorStart.AddYears(-1);
        Seed(
            Sale(BusinessA, 1, earliest, 5m),
            Sale(BusinessA, 2, CurrentStart.AddHours(1), 10m));

        var facts = await FactsFor(BusinessA);

        Assert.Equal(earliest, facts.EarliestRecordedSaleUtc);
    }

    /// <summary>
    /// A non-completed row is not a recorded sale for coverage purposes either: if the only thing in
    /// the history is a declined transaction, the comparable period is still uncovered.
    /// </summary>
    [Fact]
    public async Task A_history_of_only_non_completed_rows_reports_no_earliest_recorded_sale()
    {
        Seed(SaleWithStatus(BusinessA, 1, PriorStart.AddYears(-1), 5m, statusId: 26));

        var facts = await FactsFor(BusinessA);

        Assert.Null(facts.EarliestRecordedSaleUtc);
    }

    [Fact]
    public async Task A_business_with_no_sales_reports_zero_totals_and_no_earliest_sale()
    {
        var facts = await FactsFor(BusinessA);

        Assert.Equal(0m, facts.CurrentPeriodSales);
        Assert.Equal(0, facts.CurrentPeriodTransactionCount);
        Assert.Equal(0m, facts.PriorPeriodSales);
        Assert.Null(facts.EarliestRecordedSaleUtc);
    }

    /// <summary>
    /// The tenant boundary: another business's sales must not reach either total, nor the earliest
    /// recorded sale that decides whether a comparison is available. The scoping is the central
    /// query filter, so this proves the adapter adds no way around it.
    /// </summary>
    [Fact]
    public async Task Another_businesses_sales_reach_neither_total_nor_the_earliest_recorded_sale()
    {
        Seed(
            Sale(BusinessA, 1, CurrentStart.AddHours(1), 10m),
            Sale(BusinessA, 2, PriorStart.AddHours(1), 4m),
            Sale(BusinessB, 1, CurrentStart.AddHours(1), 5_000m),
            Sale(BusinessB, 2, PriorStart.AddHours(1), 6_000m),
            Sale(BusinessB, 3, PriorStart.AddYears(-5), 1m));

        var businessA = await FactsFor(BusinessA);
        var businessB = await FactsFor(BusinessB);

        Assert.Equal(10m, businessA.CurrentPeriodSales);
        Assert.Equal(4m, businessA.PriorPeriodSales);
        Assert.Equal(PriorStart.AddHours(1), businessA.EarliestRecordedSaleUtc);

        Assert.Equal(5_000m, businessB.CurrentPeriodSales);
        Assert.Equal(6_000m, businessB.PriorPeriodSales);
        Assert.Equal(PriorStart.AddYears(-5), businessB.EarliestRecordedSaleUtc);
    }

    /// <summary>
    /// A caller whose business could not be resolved reads nothing at all: an unresolved business is
    /// never "no filter" (AGENTS.md § Tenant ownership and data isolation).
    /// </summary>
    [Fact]
    public async Task A_caller_with_no_resolved_business_reads_nothing()
    {
        Seed(Sale(BusinessA, 1, CurrentStart.AddHours(1), 10m));

        await using var db = TestAppDbContext.Denied(_options);
        var facts = await new EfDashboardSummarySalesFactsProvider(db).GetSalesFactsAsync(
            Period(CurrentStart, CurrentEnd), Period(PriorStart, PriorEnd), CancellationToken.None);

        Assert.Equal(0m, facts.CurrentPeriodSales);
        Assert.Equal(0, facts.CurrentPeriodTransactionCount);
        Assert.Null(facts.EarliestRecordedSaleUtc);
    }
}
