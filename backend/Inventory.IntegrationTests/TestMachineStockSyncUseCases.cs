using Inventory.Application.MachineStockSync;
using Inventory.Application.Nayax;
using Moq;

namespace InventoryApi.Tests;

/// <summary>
/// Wires the Sync Restock use cases to the re-match of issue #1248, the way the production DI
/// container does.
///
/// <para>The Nayax client is only ever reached for an event that is still <em>waiting</em> for a
/// product match: a fixture whose events are all matched makes no machine-products read at all,
/// which is why these helpers default it to a client that reports no positions. A fixture about
/// matching passes its own client, so the mapping the re-match reads is the one that fixture
/// describes.</para>
/// </summary>
internal static class TestMachineStockSyncUseCases
{
    public static RematchMachineStockEvents Rematch(
        IMachineStockEventStore store, INayaxProductLinkStore productLinks, INayaxLynxClient? nayax = null) =>
        new(nayax ?? ReportsNoMachineProducts(), productLinks, store);

    public static ApplyMachineStockSync Apply(
        IMachineStockEventStore store,
        INayaxProductLinkStore productLinks,
        IMachineStockSyncLog log,
        INayaxLynxClient? nayax = null) =>
        new(store, Rematch(store, productLinks, nayax), log);

    private static INayaxLynxClient ReportsNoMachineProducts()
    {
        var client = new Mock<INayaxLynxClient>();
        client
            .Setup(x => x.GetMachineProductsFreshAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        return client.Object;
    }
}
