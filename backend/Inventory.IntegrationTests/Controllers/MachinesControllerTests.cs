using Inventory.Application.Access;
using Inventory.Application.Machines;
using Inventory.Application.MachineStock;
using Inventory.Application.MachineStockSync;
using Inventory.Application.Nayax;
using Inventory.Application.Products;
using Inventory.Application.Sites;
using Inventory.Domain.MachineStock;
using Inventory.Domain.Machines;
using Inventory.Domain.Nayax;
using InventoryApi.Controllers;
using InventoryApi.DTOs;
using InventoryApi.Tests.Application.Access;
using InventoryApi.Tests.Application.Gst;
using InventoryApi.Tests.Application.Nayax;
using InventoryApi.Tests.Application.SupportSessions;
using InventoryApi.Tests.Application.Time;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// The Sync Restock endpoints (issue #183) are thin bindings over the Application use cases
/// <see cref="SyncMachineStockFromNayax"/>/<see cref="ApplyMachineStockSync"/>: no AppDbContext,
/// Nayax client, or business logic lives in the controller itself. Since issue #302 the dashboard
/// and machine-product endpoints are the same shape - <see cref="GetMachineDashboard"/>,
/// <see cref="ListMachineDashboard"/> and <see cref="ListMachineProducts"/> are injected directly,
/// with no <c>MachineService</c> delegator between them and the action - and the actions answer the
/// API-owned <c>MachineResponse</c>/<c>ProductResponse</c> instead of EF entities.
/// </summary>
public class MachinesControllerTests
{
    private const long MachineId = 42;

    /// <summary>
    /// One pinned instant drives the dashboard clock, so the rolling Australia/Sydney comparison
    /// periods the use cases resolve cannot straddle a real midnight mid-test (issue #310).
    /// </summary>
    private static readonly FixedSydneyTime Time = FixedSydneyTime.PinnedToNow();

    private static MachinesController Controller(
        IMachineStockEventStore store,
        INayaxLynxClient? nayax = null,
        IMachineDashboardFactsStore? dashboardFacts = null,
        IProductCatalogStore? catalog = null,
        IMachineStockLedgerStore? ledger = null,
        IMachineStockObservationSource? observations = null)
    {
        var nayaxClient = nayax ?? Mock.Of<INayaxLynxClient>();
        var facts = dashboardFacts ?? Mock.Of<IMachineDashboardFactsStore>();
        var ledgerStore = ledger ?? EmptyLedger();
        var observationSource = observations ?? NoObservedSlots();
        return new(
            new GetMachineDashboard(nayaxClient, facts, Time.Clock, Time.Calendar),
            new ListMachineDashboard(nayaxClient, facts, Time.Clock, Time.Calendar),
            new ListMachineProducts(
                nayaxClient,
                catalog ?? Mock.Of<IProductCatalogStore>(),
                FakeNayaxProductLinkStore.EveryIdLinkedToItself(),
                new ResolveMachineProductPricing(NoAgreementSiteFacts(), Time.Calendar)),
            new GetMachineLatestSales(nayaxClient),
            new SyncMachineStockFromNayax(
                nayaxClient, store, FakeNayaxProductLinkStore.EveryIdLinkedToItself()),
            new ApplyMachineStockSync(store),
            new ResolveMachineStockDuplicate(store),
            new ResolveMachineStockEventsAsAlreadyRecorded(store),
            new GetMachineStockReconciliation(ledgerStore, observationSource, Time.Clock),
            new RecordMachineStockCount(ledgerStore, Time.Clock),
            // An Owner, so the dashboard actions answer the full response; the role-dependent shape
            // itself is MachineAndProductFinancialRedactionApiTests, through the real pipeline.
            new CurrentCapabilities(
                new FakeSupportSessionAccessor(),
                new FakeCurrentBusinessProvider(businessId: 1),
                new FakeRolePreviewAccessor()));
    }

    /// <summary>A ledger with no refill, sale, count or baseline for the machine (issue #56).</summary>
    private static IMachineStockLedgerStore EmptyLedger()
    {
        var ledger = new Mock<IMachineStockLedgerStore>();
        ledger.Setup(x => x.GetLedgerFactsAsync(
                It.IsAny<long>(), It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MachineStockLedgerFacts([], [], [], [], new Dictionary<long, string>(), 0, 0));
        return ledger.Object;
    }

    private static IMachineStockObservationSource NoObservedSlots()
    {
        var observations = new Mock<IMachineStockObservationSource>();
        observations.Setup(x => x.GetObservationAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MachineStockObservation(Time.Clock.UtcNow, []));
        return observations.Object;
    }

    [Fact]
    public async Task SyncRestock_returns_the_use_case_preview()
    {
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachineLastAlertsAsync(MachineId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var store = new Mock<IMachineStockEventStore>();
        store.Setup(x => x.GetImportedNayaxEventLogIdsAsync(MachineId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        store.Setup(x => x.GetUnprocessedEventsAsync(
                MachineId, It.IsAny<CancellationToken>(), It.IsAny<DateTime?>(), It.IsAny<bool>()))
            .ReturnsAsync(new MachineStockEventsPage([], 0));
        store.Setup(x => x.GetManualMachineRefillsAsync(MachineId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var result = await Controller(store.Object, nayax.Object).SyncRestock(MachineId, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var preview = Assert.IsType<NayaxMachineStockSyncPreviewDto>(ok.Value);
        Assert.Equal(MachineId, preview.MachineId);
        Assert.Equal("No new Nayax stock-adjustment alerts to review.", preview.Message);
    }

    [Fact]
    public async Task SyncRestock_passes_the_from_date_and_show_reconciled_query_parameters_through()
    {
        var expectedUtc = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var fromDate = new DateTimeOffset(expectedUtc);
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachineLastAlertsAsync(MachineId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var store = new Mock<IMachineStockEventStore>();
        store.Setup(x => x.GetImportedNayaxEventLogIdsAsync(MachineId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        store.Setup(x => x.GetUnprocessedEventsAsync(MachineId, It.IsAny<CancellationToken>(), expectedUtc, true))
            .ReturnsAsync(new MachineStockEventsPage([], 3));
        store.Setup(x => x.GetManualMachineRefillsAsync(MachineId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var result = await Controller(store.Object, nayax.Object)
            .SyncRestock(MachineId, CancellationToken.None, fromDate, includeReconciled: true);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var preview = Assert.IsType<NayaxMachineStockSyncPreviewDto>(ok.Value);
        Assert.Equal(3, preview.HiddenReconciledCount);
        store.Verify(
            x => x.GetUnprocessedEventsAsync(MachineId, It.IsAny<CancellationToken>(), expectedUtc, true), Times.Once);
    }

    /// <summary>
    /// Issue #218: <c>fromDate</c> is bound as <see cref="DateTimeOffset"/> precisely so its
    /// instant is unambiguous regardless of the server process's local time zone. This proves the
    /// controller converts a non-zero-offset value (as a client in any time zone could send) to the
    /// exact same UTC instant, rather than the server-local-time-zone-dependent shift a plain
    /// <c>DateTime</c> query parameter would apply.
    /// </summary>
    [Fact]
    public async Task SyncRestock_converts_a_non_utc_offset_from_date_to_its_exact_utc_instant()
    {
        var expectedUtc = new DateTime(2026, 8, 31, 14, 0, 0, DateTimeKind.Utc);
        var fromDate = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.FromHours(10));
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachineLastAlertsAsync(MachineId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var store = new Mock<IMachineStockEventStore>();
        store.Setup(x => x.GetImportedNayaxEventLogIdsAsync(MachineId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        store.Setup(x => x.GetUnprocessedEventsAsync(MachineId, It.IsAny<CancellationToken>(), expectedUtc, false))
            .ReturnsAsync(new MachineStockEventsPage([], 0));
        store.Setup(x => x.GetManualMachineRefillsAsync(MachineId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        await Controller(store.Object, nayax.Object).SyncRestock(MachineId, CancellationToken.None, fromDate);

        store.Verify(
            x => x.GetUnprocessedEventsAsync(MachineId, It.IsAny<CancellationToken>(), expectedUtc, false), Times.Once);
    }

    [Fact]
    public async Task ApplySyncRestock_passes_the_requested_event_ids_through()
    {
        var store = new Mock<IMachineStockEventStore>();
        store.Setup(x => x.FindEventAsync(MachineId, 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MachineStockEventState(
                7, 5001, DateTime.UtcNow, NayaxStockEventMatchStatus.Matched, null,
                NayaxStockEventProcessingStatus.Unprocessed, 200, 2, null, NayaxDuplicateResolution.None));
        store.Setup(x => x.FindStorageProductAsync(200, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NayaxStockSyncProduct(200, "Coke 375mL", 10));
        store.Setup(x => x.GetManualMachineRefillsAsync(MachineId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        store.Setup(x => x.ApplyRefillAsync(
                7, MachineId, 5001, 200, 2, NayaxDuplicateResolution.None, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MachineRefillApplication(true, 99));

        var result = await Controller(store.Object)
            .ApplySyncRestock(MachineId, new NayaxStockEventApplyRequestDto([7]), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<NayaxMachineStockApplyResponseDto>(ok.Value);
        var applied = Assert.Single(response.Results);
        Assert.Equal(7, applied.EventId);
        Assert.Equal(NayaxStockEventApplyOutcome.Applied, applied.Outcome);
        Assert.Equal(99, applied.StockAdjustmentId);
        store.VerifyAll();
    }

    [Fact]
    public async Task ResolveSyncRestockManually_passes_the_requested_event_ids_through()
    {
        var store = new Mock<IMachineStockEventStore>();
        store.Setup(x => x.GetManualMachineRefillsAsync(MachineId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        store.Setup(x => x.FindEventAsync(MachineId, 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MachineStockEventState(
                7, 5001, DateTime.UtcNow, NayaxStockEventMatchStatus.NeedsReview, "Unknown MDB",
                NayaxStockEventProcessingStatus.Unprocessed, null, null, null, NayaxDuplicateResolution.None));
        store.Setup(x => x.ReconcileAsManualDuplicateAsync(7, MachineId, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await Controller(store.Object)
            .ResolveSyncRestockManually(MachineId, new NayaxResolveManyAsAlreadyRecordedRequestDto([7]), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<NayaxMachineStockApplyResponseDto>(ok.Value);
        var resolved = Assert.Single(response.Results);
        Assert.Equal(7, resolved.EventId);
        Assert.Equal(NayaxStockEventApplyOutcome.Reconciled, resolved.Outcome);
        store.VerifyAll();
    }

    /// <summary>
    /// Issue #302: the dashboard endpoints answer the API-owned <see cref="MachineResponse"/>, not
    /// the <c>Inventory.Infrastructure.Models.Machine</c> type they used to serialise, with the same 200 and the
    /// same already-resolved values the use case returns.
    /// </summary>
    [Fact]
    public async Task GetById_answers_the_machine_dashboard_use_case_result()
    {
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachineAsync(MachineId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NayaxMachine
            {
                MachineID = MachineId,
                MachineName = "Pavillion Left",
                ActorID = 7,
                CustomerID = 91,
            });

        var result = await Controller(Mock.Of<IMachineStockEventStore>(), nayax.Object, EmptyCostedFacts())
            .GetById(MachineId);

        var machine = Assert.IsType<MachineResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(MachineId, machine.MachineID);
        Assert.Equal("Pavillion Left", machine.MachineName);
        Assert.Equal(7, machine.ActorID);
        Assert.Equal(0m, machine.TodayGrossRevenue);
    }

    /// <summary>An id Nayax does not know is still a 404, not an empty machine.</summary>
    [Fact]
    public async Task GetById_answers_not_found_for_a_machine_nayax_does_not_return()
    {
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachineAsync(MachineId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((NayaxMachine)null!);

        var result = await Controller(Mock.Of<IMachineStockEventStore>(), nayax.Object, EmptyCostedFacts())
            .GetById(MachineId);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task GetAll_answers_the_machine_listing_use_case_result()
    {
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachinesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new NayaxMachine { MachineID = MachineId, MachineName = "Pavillion Left", CustomerID = 91 }]);

        var result = await Controller(Mock.Of<IMachineStockEventStore>(), nayax.Object, EmptyCostedFacts())
            .GetAll();

        var machines = Assert.IsType<List<MachineResponse>>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(MachineId, Assert.Single(machines).MachineID);
    }

    /// <summary>
    /// The machine-product endpoint answers the same API-owned <see cref="ProductResponse"/> the
    /// catalogue endpoints do, with the machine slot's own price, MDB code and stock overlaid on it
    /// in place of the storage product's.
    /// </summary>
    [Fact]
    public async Task GetMachineProducts_answers_the_catalogue_response_with_the_slot_values_overlaid()
    {
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachineAsync(MachineId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NayaxMachine { MachineID = MachineId, CustomerID = 91 });
        nayax.Setup(x => x.GetMachineProductsAsync(MachineId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new NayaxMachineProduct
                {
                    NayaxProductID = 200, RetailPrice = 5m, CommissionValue = 10m, MDBCode = 13, PAR = 6,
                    MissingStockByMDB = 4,
                },
            ]);
        var catalog = new Mock<IProductCatalogStore>();
        catalog.Setup(x => x.ListUnorderedAsync(It.IsAny<ProductCatalogFilter>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new ProductRecord
                {
                    Id = 200,
                    Name = "Coke",
                    UnitPrice = 3.50m,
                    AverageUnitCost = 1.25m,
                    QuantityInStock = 40,
                    LowStockThreshold = 0,
                    RestockTo = 0,
                    IsActive = true,
                    CreatedAt = Time.NowUtc,
                    UpdatedAt = Time.NowUtc,
                },
            ]);

        var result = await Controller(
                Mock.Of<IMachineStockEventStore>(), nayax.Object, EmptyCostedFacts(), catalog.Object)
            .GetMachineProducts(MachineId);

        var products = Assert.IsType<List<ProductResponse>>(Assert.IsType<OkObjectResult>(result.Result).Value);
        var product = Assert.Single(products);
        Assert.Equal(200, product.Id);
        Assert.Equal("Coke", product.Name);
        Assert.Equal(5m, product.MachinePrice);
        Assert.Equal(10m, product.CommissionValue);
        Assert.Equal(13, product.MdbCode);
        Assert.Equal(6, product.MaxStockInMachine);

        // The slot's stock (PAR 6 less 4 missing), not the storage product's 40.
        Assert.Equal(2, product.QuantityInStock);
    }

    /// <summary>
    /// The machine-stock ledger endpoint (issue #56) is the same thin shape: it binds the route id,
    /// invokes the use case and returns its result, with no second machine-stock calculation of its
    /// own.
    /// </summary>
    [Fact]
    public async Task GetStockLedger_answers_the_reconciliation_use_case_result()
    {
        var countedAt = Time.NowUtc.AddDays(-1);
        var ledger = new Mock<IMachineStockLedgerStore>();
        ledger.Setup(x => x.GetLedgerFactsAsync(
                MachineId, It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MachineStockLedgerFacts(
                [],
                [],
                [new MachineStockCountFact(1, 200, 10, countedAt)],
                [],
                new Dictionary<long, string> { [200] = "Coke" },
                0,
                0));
        var observations = new Mock<IMachineStockObservationSource>();
        observations.Setup(x => x.GetObservationAsync(MachineId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MachineStockObservation(
                Time.NowUtc, [new MachineStockObservationSlot(200, 12, 5)]));

        var result = await Controller(
                Mock.Of<IMachineStockEventStore>(), ledger: ledger.Object, observations: observations.Object)
            .GetStockLedger(MachineId, CancellationToken.None);

        var reconciliation = Assert.IsType<MachineStockReconciliationDto>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(MachineId, reconciliation.MachineId);
        var row = Assert.Single(reconciliation.Products);
        Assert.Equal("Coke", row.ProductName);
        Assert.Equal(10, row.ExpectedQuantity);
        Assert.Equal(7, row.ObservedQuantity);
        Assert.Equal(3, row.ShrinkageQuantity);
    }

    [Fact]
    public async Task RecordStockCount_returns_the_recorded_count()
    {
        var ledger = new Mock<IMachineStockLedgerStore>();
        ledger.Setup(x => x.RecordCountAsync(
                MachineId, 200, 9, It.IsAny<DateTime>(), "Counted", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MachineStockCountRecord(5, MachineId, 200, "Coke", 9, Time.NowUtc, "Counted"));

        var result = await Controller(Mock.Of<IMachineStockEventStore>(), ledger: ledger.Object)
            .RecordStockCount(MachineId, new MachineStockCountRequestDto(200, 9, "Counted"), CancellationToken.None);

        var count = Assert.IsType<MachineStockCountResultDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(5, count.CountId);
        Assert.Equal(9, count.CountedQuantity);
        Assert.Equal("Coke", count.ProductName);
    }

    [Fact]
    public async Task RecordStockCount_answers_not_found_for_a_product_the_business_does_not_have()
    {
        var ledger = new Mock<IMachineStockLedgerStore>();
        ledger.Setup(x => x.RecordCountAsync(
                MachineId, 200, 9, It.IsAny<DateTime>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((MachineStockCountRecord?)null);

        var result = await Controller(Mock.Of<IMachineStockEventStore>(), ledger: ledger.Object)
            .RecordStockCount(MachineId, new MachineStockCountRequestDto(200, 9, null), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    /// <summary>
    /// A site with no commission agreement and no effective Nayax fee rate - the valid
    /// zero-commission case. The suggested-pricing rules themselves belong to
    /// <c>MachineProfitabilityTests</c> and the Domain policy tests; these tests only need the
    /// listing to reach the response mapping.
    /// </summary>
    private static ISiteFactsStore NoAgreementSiteFacts()
    {
        var facts = new Mock<ISiteFactsStore>();
        facts.Setup(x => x.ResolveCardCommissionAsync(
                It.IsAny<long>(),
                It.IsAny<DateTime>(),
                It.IsAny<IReadOnlyCollection<decimal>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SiteCardCommissionResolution(false, new Dictionary<decimal, decimal>()));
        facts.Setup(x => x.ResolveEffectiveFeeExGstAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((decimal?)null);
        return facts.Object;
    }

    /// <summary>
    /// A dashboard facts store with no sales and complete costing: enough for the endpoints' own
    /// contract, with the period and status rules themselves covered by the Domain/Application
    /// tests that own them.
    /// </summary>
    private static IMachineDashboardFactsStore EmptyCostedFacts()
    {
        var period = new MachineDashboardPeriodFacts(
            0m, new MachineDashboardDirectProfitInputs(false, 0m, false, 0m));
        var facts = new Mock<IMachineDashboardFactsStore>();
        facts.Setup(x => x.GetFactsAsync(
                It.IsAny<long>(), It.IsAny<long?>(), It.IsAny<MachineDashboardWindow>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MachineDashboardFacts(
                period, period, period, period, period, period,
                new MachineProfitabilityStatusInputs(false, false, true, false)));
        return facts.Object;
    }
}
