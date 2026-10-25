using Inventory.Application.Nayax;
using Inventory.Application.PickList;
using InventoryApi.Tests.Application.Nayax;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Application.PickList;

/// <summary>
/// How the Pick List identifies a product (issue #733): a machine selection's Nayax product id is
/// resolved through the Nayax link table before the storage quantity is read, so the row that is
/// shown - and the storage it is compared against - belong to the product the link names.
/// </summary>
public class GetPickListNayaxLinkTests
{
    private static Mock<INayaxLynxClient> MachineReporting(long machineId, params NayaxMachineProduct[] selections)
    {
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachineProductsFreshAsync(machineId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([.. selections]);
        return nayax;
    }

    private static Mock<IPickListStorageStockStore> StorageWith(
        params (long ProductId, string Name, int QuantityInStock)[] products)
    {
        var storage = new Mock<IPickListStorageStockStore>();
        storage
            .Setup(x => x.GetStorageProductsAsync(It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<long> productIds, CancellationToken _) => productIds
                .Join(products, id => id, product => product.ProductId, (_, product) => product)
                .ToDictionary(
                    product => product.ProductId,
                    product => new PickListStorageProduct(
                        product.ProductId, product.Name, product.QuantityInStock, IsActive: true)));
        return storage;
    }

    [Fact]
    public async Task A_selection_is_picked_for_the_linked_local_product()
    {
        var nayax = MachineReporting(
            1, new NayaxMachineProduct { NayaxProductID = 100, PAR = 10, MissingStockByMDB = 4 });
        var storage = StorageWith((4100, "Coke Zero", 50));

        var result = await new GetPickList(
                nayax.Object, storage.Object, FakeNayaxProductLinkStore.Linking((100, 4100)))
            .Handle([1], CancellationToken.None);

        var product = Assert.Single(result.Products);
        Assert.Equal(4100, product.ProductId);
        Assert.Equal("Coke Zero", product.ProductName);
        Assert.Equal(4, product.TotalQuantityToPick);
    }

    /// <summary>
    /// A local product numbered like somebody's Nayax product is not that product. The selection has
    /// no link, so it is excluded exactly as a selection with no local catalogue row already was -
    /// never shown against the storage of the product that shares its number.
    /// </summary>
    [Fact]
    public async Task An_unlinked_selection_is_not_picked_for_the_product_whose_id_equals_it()
    {
        var nayax = MachineReporting(
            1, new NayaxMachineProduct { NayaxProductID = 100, PAR = 10, MissingStockByMDB = 4 });
        var storage = StorageWith((100, "Unrelated product numbered 100", 50));

        var result = await new GetPickList(
                nayax.Object, storage.Object, FakeNayaxProductLinkStore.Linking((200, 4200)))
            .Handle([1], CancellationToken.None);

        Assert.Empty(result.Products);
    }

    /// <summary>
    /// The storage read is given local product ids, not the remote ones: a persistence port never
    /// receives a Nayax id as if it were a local key.
    /// </summary>
    [Fact]
    public async Task The_storage_read_is_asked_for_local_product_ids()
    {
        var nayax = MachineReporting(
            1, new NayaxMachineProduct { NayaxProductID = 100, PAR = 10, MissingStockByMDB = 4 });
        var storage = StorageWith((4100, "Coke Zero", 50));

        await new GetPickList(nayax.Object, storage.Object, FakeNayaxProductLinkStore.Linking((100, 4100)))
            .Handle([1], CancellationToken.None);

        storage.Verify(
            x => x.GetStorageProductsAsync(
                It.Is<IReadOnlyCollection<long>>(ids => ids.SequenceEqual(new[] { 4100L })),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
