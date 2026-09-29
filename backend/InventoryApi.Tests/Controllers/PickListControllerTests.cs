using Inventory.Application.Nayax;
using Inventory.Application.PickList;
using InventoryApi.Controllers;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Controllers;

public class PickListControllerTests
{
    private static Mock<IPickListStorageStockStore> EmptyStorage()
    {
        var store = new Mock<IPickListStorageStockStore>();
        store.Setup(x => x.GetStorageProductsAsync(It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<long, PickListStorageProduct>)new Dictionary<long, PickListStorageProduct>());
        return store;
    }

    [Fact]
    public async Task Get_ReturnsBadRequest_WhenNoMachineIdsAreGiven()
    {
        var nayax = new Mock<INayaxLynxClient>(MockBehavior.Strict);
        var controller = new PickListController(new GetPickList(nayax.Object, EmptyStorage().Object));

        var result = await controller.Get([], CancellationToken.None);

        Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Get_ReturnsThePickListProjection_ForTheSelectedMachines()
    {
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachineProductsAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachineProduct> { new() { NayaxProductID = 100, PAR = 10, MissingStockByMDB = 4 } });
        var storage = new Mock<IPickListStorageStockStore>();
        storage.Setup(x => x.GetStorageProductsAsync(It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<long, PickListStorageProduct>)new Dictionary<long, PickListStorageProduct>
            {
                [100] = new PickListStorageProduct(100, "Coke Zero", 50),
            });
        var controller = new PickListController(new GetPickList(nayax.Object, storage.Object));

        var result = await controller.Get([1], CancellationToken.None);

        var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result.Result);
        var body = Assert.IsType<PickListResult>(ok.Value);
        var product = Assert.Single(body.Products);
        Assert.Equal(4, product.TotalQuantityToPick);
    }
}
