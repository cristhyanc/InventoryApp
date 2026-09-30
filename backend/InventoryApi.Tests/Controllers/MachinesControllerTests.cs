using Inventory.Application.MachineStockSync;
using Inventory.Application.Nayax;
using Inventory.Domain.Nayax;
using InventoryApi.Controllers;
using InventoryApi.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// The Sync Restock endpoints (issue #183) are thin bindings over the Application use cases
/// <see cref="SyncMachineStockFromNayax"/>/<see cref="ApplyMachineStockSync"/>: no AppDbContext,
/// Nayax client, or business logic lives in the controller itself.
/// </summary>
public class MachinesControllerTests
{
    private const long MachineId = 42;

    private static MachinesController Controller(IMachineStockEventStore store, INayaxLynxClient? nayax = null) =>
        new(
            Mock.Of<IMachineService>(),
            new SyncMachineStockFromNayax(nayax ?? Mock.Of<INayaxLynxClient>(), store),
            new ApplyMachineStockSync(store),
            new ResolveMachineStockDuplicate(store),
            new ResolveMachineStockEventsAsAlreadyRecorded(store));

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
}
