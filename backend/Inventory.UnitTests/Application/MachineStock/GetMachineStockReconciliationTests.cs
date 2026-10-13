using Inventory.Application.MachineStock;
using Inventory.Application.Nayax;
using Inventory.Domain.MachineStock;
using Inventory.Domain.Nayax;
using InventoryApi.Tests.Application.Time;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Application.MachineStock;

/// <summary>
/// The machine-stock reconciliation read (issue #56). These tests use mocked ports only; the
/// EF-backed ledger adapter and the Nayax observation adapter have their own tests.
///
/// What is asserted here is the orchestration the Domain cannot see: that a business with no usable
/// Nayax connection still gets its locally-derived expected stock with a data-quality state instead
/// of an error, that a real failure is not disguised as an empty machine, and that an observed slot
/// for a product outside the caller's own catalogue is dropped rather than reported.
/// </summary>
public class GetMachineStockReconciliationTests
{
    private const long MachineId = 42;
    private const long Product = 500;
    private static readonly DateTime Now = new(2026, 3, 10, 2, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime CountedAt = new(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc);

    private static MachineStockLedgerFacts Facts(
        IReadOnlyList<MachineStockCountFact>? counts = null,
        IReadOnlyList<MachineRefillFact>? refills = null,
        IReadOnlyList<MachineSaleFact>? sales = null,
        IReadOnlyDictionary<long, string>? productNames = null,
        int pendingNayaxStockEventCount = 0,
        int unattributedCompletedSaleCount = 0) =>
        new(
            refills ?? [],
            sales ?? [],
            counts ?? [],
            [],
            productNames ?? new Dictionary<long, string> { [Product] = "Coke Zero" },
            pendingNayaxStockEventCount,
            unattributedCompletedSaleCount);

    private static Mock<IMachineStockLedgerStore> StoreWith(MachineStockLedgerFacts facts)
    {
        var store = new Mock<IMachineStockLedgerStore>();
        store.Setup(x => x.GetLedgerFactsAsync(
                MachineId, It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(facts);
        return store;
    }

    private static Mock<IMachineStockObservationSource> ObservationOf(
        params MachineStockObservationSlot[] slots)
    {
        var observations = new Mock<IMachineStockObservationSource>();
        observations.Setup(x => x.GetObservationAsync(MachineId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MachineStockObservation(Now, slots));
        return observations;
    }

    private static GetMachineStockReconciliation UseCase(
        Mock<IMachineStockLedgerStore> store, Mock<IMachineStockObservationSource> observations) =>
        new(store.Object, observations.Object, new FakeClock(Now));

    [Fact]
    public async Task It_reports_expected_observed_and_shrinkage_per_product()
    {
        var store = StoreWith(Facts(
            counts: [new MachineStockCountFact(1, Product, 20, CountedAt)],
            sales: [new MachineSaleFact(9001, Product, CountedAt.AddDays(1))]));
        var observations = ObservationOf(new MachineStockObservationSlot(Product, 24, 8));

        var result = await UseCase(store, observations).Handle(MachineId, CancellationToken.None);

        Assert.Equal(MachineId, result.MachineId);
        Assert.Equal(Now, result.AsOfUtc);
        Assert.Equal(Now, result.ObservedAtUtc);
        var row = Assert.Single(result.Products);
        Assert.Equal("Coke Zero", row.ProductName);
        Assert.Equal(19, row.ExpectedQuantity);
        Assert.Equal(16, row.ObservedQuantity);
        Assert.Equal(-3, row.DiscrepancyQuantity);
        Assert.Equal(3, row.ShrinkageQuantity);
        Assert.Equal(MachineStockReconciliationStatus.Shortage, row.Status);
        Assert.Empty(result.DataQuality);
    }

    [Fact]
    public async Task An_unconnected_business_still_gets_expected_stock_with_a_data_quality_state()
    {
        var store = StoreWith(Facts(counts: [new MachineStockCountFact(1, Product, 20, CountedAt)]));
        var observations = new Mock<IMachineStockObservationSource>();
        observations.Setup(x => x.GetObservationAsync(MachineId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NayaxNotConnectedException(NayaxConnectionStatus.NotConfigured));

        var result = await UseCase(store, observations).Handle(MachineId, CancellationToken.None);

        Assert.Null(result.ObservedAtUtc);
        Assert.Contains(
            MachineStockDataQualityIssueCodes.ObservationSourceUnavailable,
            result.DataQuality.Select(issue => issue.Code));
        var row = Assert.Single(result.Products);
        Assert.Equal(20, row.ExpectedQuantity);
        Assert.Null(row.ObservedQuantity);
        Assert.Null(row.DiscrepancyQuantity);
        Assert.Equal(MachineStockReconciliationStatus.Unknown, row.Status);
    }

    [Fact]
    public async Task A_permission_the_nayax_account_has_not_granted_is_reported_the_same_way()
    {
        var store = StoreWith(Facts(counts: [new MachineStockCountFact(1, Product, 20, CountedAt)]));
        var observations = new Mock<IMachineStockObservationSource>();
        observations.Setup(x => x.GetObservationAsync(MachineId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NayaxPermissionNotGrantedException("GetMachineProductsAsync"));

        var result = await UseCase(store, observations).Handle(MachineId, CancellationToken.None);

        Assert.Null(result.ObservedAtUtc);
        Assert.Contains(
            MachineStockDataQualityIssueCodes.ObservationSourceUnavailable,
            result.DataQuality.Select(issue => issue.Code));
    }

    /// <summary>
    /// A transport failure is not a data-quality state: reporting it as "nothing observed" would
    /// present a machine as unreadable when the real answer is that the request failed.
    /// </summary>
    [Fact]
    public async Task A_transport_failure_propagates_instead_of_becoming_an_unobserved_machine()
    {
        var store = StoreWith(Facts());
        var observations = new Mock<IMachineStockObservationSource>();
        observations.Setup(x => x.GetObservationAsync(MachineId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("upstream down"));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => UseCase(store, observations).Handle(MachineId, CancellationToken.None));
    }

    [Fact]
    public async Task An_observed_slot_outside_the_callers_catalogue_is_dropped()
    {
        var store = StoreWith(Facts(productNames: new Dictionary<long, string> { [Product] = "Coke Zero" }));
        var observations = ObservationOf(
            new MachineStockObservationSlot(Product, 10, 2),
            new MachineStockObservationSlot(999, 10, 0));

        var result = await UseCase(store, observations).Handle(MachineId, CancellationToken.None);

        var row = Assert.Single(result.Products);
        Assert.Equal(Product, row.ProductId);
    }

    [Fact]
    public async Task Unprocessed_nayax_events_and_unattributed_sales_are_reported_for_the_machine()
    {
        var store = StoreWith(Facts(
            counts: [new MachineStockCountFact(1, Product, 20, CountedAt)],
            pendingNayaxStockEventCount: 2,
            unattributedCompletedSaleCount: 5));
        var observations = ObservationOf(new MachineStockObservationSlot(Product, 20, 0));

        var result = await UseCase(store, observations).Handle(MachineId, CancellationToken.None);

        Assert.Equal(
            [
                MachineStockDataQualityIssueCodes.PendingNayaxStockEvents,
                MachineStockDataQualityIssueCodes.UnattributedCompletedSales,
            ],
            result.DataQuality.Select(issue => issue.Code));
    }

    [Fact]
    public async Task The_observed_products_are_passed_to_the_ledger_read_so_their_names_resolve()
    {
        var store = StoreWith(Facts());
        var observations = ObservationOf(
            new MachineStockObservationSlot(Product, 10, 2),
            new MachineStockObservationSlot(Product, 4, 1));

        await UseCase(store, observations).Handle(MachineId, CancellationToken.None);

        store.Verify(
            x => x.GetLedgerFactsAsync(
                MachineId,
                It.Is<IReadOnlyCollection<long>>(ids => ids.Count == 1 && ids.Contains(Product)),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
