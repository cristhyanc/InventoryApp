using Inventory.Application.Nayax;
using Inventory.Application.Reorder;
using InventoryApi.Tests.Application.Nayax;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Application.Reorder;

/// <summary>
/// How the reorder aggregation identifies a product (issue #733): the Nayax product id each machine
/// selection reports is resolved through the Nayax link table, and both the per-product totals and
/// the retained selections carry the local product id the link names.
///
/// The two cases are the ones that tell the two implementations apart. A link whose local id differs
/// from the remote id can only be followed by reading the link; and a local product that merely
/// happens to be numbered like somebody's Nayax id is not that Nayax product, so the need must not
/// be attributed to it.
/// </summary>
public class CalculateReorderNeedsNayaxLinkTests
{
    private static Mock<IOutstandingSupplierOrderQuantityStore> EmptyOutstandingStore()
    {
        var store = new Mock<IOutstandingSupplierOrderQuantityStore>();
        store.Setup(x => x.GetOutstandingQuantitiesByProductAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<long, decimal>());
        return store;
    }

    private static Mock<INayaxLynxClient> MachineReporting(long machineId, params NayaxMachineProduct[] selections)
    {
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachinesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new NayaxMachine { MachineID = machineId }]);
        nayax.Setup(x => x.GetMachineProductsAsync(machineId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([.. selections]);
        return nayax;
    }

    private static Task<ReorderNeedsResult> HandleAsync(
        Mock<INayaxLynxClient> nayax, INayaxProductLinkStore productLinks) =>
        new CalculateReorderNeeds(nayax.Object, EmptyOutstandingStore().Object, productLinks)
            .Handle(CancellationToken.None);

    [Fact]
    public async Task The_replenishment_need_is_attributed_to_the_linked_local_product()
    {
        var nayax = MachineReporting(1, new NayaxMachineProduct { NayaxProductID = 100, MissingStockByMDB = 3 });

        var result = await HandleAsync(nayax, FakeNayaxProductLinkStore.Linking((100, 4100)));

        Assert.Equal(3, result.MachineReplenishmentNeedByProductId[4100]);
        Assert.False(result.MachineReplenishmentNeedByProductId.ContainsKey(100));
        var selection = Assert.Single(result.MachineSelections);
        Assert.Equal(4100, selection.ProductId);
    }

    [Fact]
    public async Task An_unlinked_nayax_product_needs_nothing_and_its_selection_names_no_product()
    {
        var nayax = MachineReporting(
            1,
            new NayaxMachineProduct { NayaxProductID = 100, MissingStockByMDB = 3 },
            new NayaxMachineProduct { NayaxProductID = 7, MissingStockByMDB = 9 });

        // Only Nayax product 100 is linked, and it is linked to local product 7. The second
        // selection names Nayax product 7, which this business has no link for: it must not be
        // attributed to local product 7, and it must not become a replenishment need of its own.
        var result = await HandleAsync(nayax, FakeNayaxProductLinkStore.Linking((100, 7)));

        Assert.Equal(3, result.MachineReplenishmentNeedByProductId[7]);
        Assert.Single(result.MachineReplenishmentNeedByProductId);
        Assert.Contains(result.MachineSelections, selection => selection.ProductId is null);
    }
}
