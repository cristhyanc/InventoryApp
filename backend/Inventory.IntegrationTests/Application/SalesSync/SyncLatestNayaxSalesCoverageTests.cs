using Inventory.Application.Nayax;
using Inventory.Application.SalesSync;
using Inventory.Domain.Providers;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Persistence;
using InventoryApi.Tests.Application.Providers;
using InventoryApi.Tests.Application.Time;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Application.SalesSync;

/// <summary>
/// Issue #730 (parent #329 decision P9): what the latest-sales synchronization records as covered,
/// and - more importantly - when it records nothing.
///
/// A coverage row is the only evidence a report has that an empty period is genuinely empty, so a
/// run that did not account for everything it claims must leave no row at all. These tests pin that
/// asymmetry: one successful run claims the window it demonstrably read; a run that failed for one
/// machine, or that read a sale it could not store, claims nothing and leaves the period unverified.
/// </summary>
public class SyncLatestNayaxSalesCoverageTests
{
    private static readonly DateTime RunAt = new(2026, 10, 15, 0, 0, 0, DateTimeKind.Utc);

    private static AppDbContext CreateDb() =>
        TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static SyncLatestNayaxSales UseCase(
        AppDbContext db, INayaxLynxClient nayax, FakeProviderDataCoverageStore coverage)
    {
        var rebuild = TestCostingUseCases.Rebuild(db);
        return TestSalesSync.UseCase(
            nayax,
            new EfLatestNayaxSalesStore(
                db,
                TestCostingUseCases.CostSale(db, rebuild),
                rebuild,
                InventoryApi.Tests.Application.Nayax.FakeNayaxProductLinkStore.EveryIdLinkedToItself()),
            new FakeClock(RunAt),
            coverage);
    }

    private static NayaxLastSalesReport Sale(long transactionId, long machineId, DateTime? authorizedUtc) =>
        new()
        {
            TransactionID = transactionId,
            MachineID = machineId,
            SettlementValue = 3m,
            AuthorizationDateTimeGmt = authorizedUtc is { } instant ? new DateTimeOffset(instant, TimeSpan.Zero) : null
        };

    private static Mock<INayaxLynxClient> Fleet(params long[] machineIds)
    {
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachinesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(machineIds.Select(id => new NayaxMachine { MachineID = id, MachineName = $"Machine {id}" }).ToList());
        return nayax;
    }

    [Fact]
    public async Task A_successful_run_claims_from_the_latest_machine_window_start_to_when_it_began_reading()
    {
        // Machine 1's window reaches back to the 1st and machine 2's only to the 5th, so the
        // business-wide claim can only start on the 5th: before that, machine 2 might hold sales
        // this run never saw.
        await using var db = CreateDb();
        var nayax = Fleet(1, 2);
        nayax.Setup(x => x.GetMachineLastSalesAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Sale(100, 1, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc))]);
        nayax.Setup(x => x.GetMachineLastSalesAsync(2, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Sale(200, 2, new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc))]);
        var coverage = new FakeProviderDataCoverageStore();

        await UseCase(db, nayax.Object, coverage).Handle();

        var recorded = Assert.Single(coverage.Recorded);
        Assert.Equal(ExternalProvider.Nayax, recorded.Provider);
        Assert.Equal(ProviderDataSource.SalesSync, recorded.Source);
        Assert.Equal(new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc), recorded.FromUtc);
        Assert.Equal(RunAt, recorded.ToUtc);
    }

    [Fact]
    public async Task A_machine_with_no_sales_in_its_window_does_not_narrow_the_claim()
    {
        // An empty "last N sales" answer is the provider saying that machine has nothing to miss,
        // so it constrains nothing - otherwise one brand-new machine would block coverage forever.
        await using var db = CreateDb();
        var nayax = Fleet(1, 2);
        nayax.Setup(x => x.GetMachineLastSalesAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Sale(100, 1, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc))]);
        nayax.Setup(x => x.GetMachineLastSalesAsync(2, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var coverage = new FakeProviderDataCoverageStore();

        await UseCase(db, nayax.Object, coverage).Handle();

        Assert.Equal(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), Assert.Single(coverage.Recorded).FromUtc);
    }

    [Fact]
    public async Task A_run_that_fails_for_one_machine_records_no_coverage_at_all()
    {
        await using var db = CreateDb();
        var nayax = Fleet(1, 2);
        nayax.Setup(x => x.GetMachineLastSalesAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Sale(100, 1, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc))]);
        nayax.Setup(x => x.GetMachineLastSalesAsync(2, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("machine 2 is unreachable"));
        var coverage = new FakeProviderDataCoverageStore();

        await Assert.ThrowsAsync<HttpRequestException>(() => UseCase(db, nayax.Object, coverage).Handle());

        Assert.Empty(coverage.Recorded);
        Assert.Empty(await db.NayaxSales.ToListAsync());
    }

    [Fact]
    public async Task A_cancelled_run_records_no_coverage()
    {
        await using var db = CreateDb();
        var nayax = Fleet(1);
        nayax.Setup(x => x.GetMachineLastSalesAsync(1, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        var coverage = new FakeProviderDataCoverageStore();

        await Assert.ThrowsAsync<OperationCanceledException>(() => UseCase(db, nayax.Object, coverage).Handle());

        Assert.Empty(coverage.Recorded);
    }

    [Fact]
    public async Task A_batch_holding_a_sale_with_no_authoritative_instant_records_no_coverage()
    {
        // The store refuses to import a payload item carrying no AuthorizationDateTimeGMT (issue
        // #380), so a run that read one did not account for its own range and must claim nothing.
        await using var db = CreateDb();
        var nayax = Fleet(1);
        nayax.Setup(x => x.GetMachineLastSalesAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                Sale(100, 1, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)),
                Sale(101, 1, null)
            ]);
        var coverage = new FakeProviderDataCoverageStore();

        await UseCase(db, nayax.Object, coverage).Handle();

        Assert.Empty(coverage.Recorded);
        Assert.Single(await db.NayaxSales.ToListAsync());
    }

    [Fact]
    public async Task A_run_that_saw_no_dated_sale_anywhere_claims_nothing_rather_than_an_instant()
    {
        // Every window was empty. That says nothing about how far back any window reaches, so there
        // is no range to claim and the period stays unverified rather than falsely complete.
        await using var db = CreateDb();
        var nayax = Fleet(1, 2);
        nayax.Setup(x => x.GetMachineLastSalesAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var coverage = new FakeProviderDataCoverageStore();

        await UseCase(db, nayax.Object, coverage).Handle();

        Assert.Empty(coverage.Recorded);
    }
}
