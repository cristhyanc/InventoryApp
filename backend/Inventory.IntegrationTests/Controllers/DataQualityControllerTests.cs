using Inventory.Application.CatalogReconciliation;
using Inventory.Domain.CatalogReconciliation;
using InventoryApi.Controllers;
using InventoryApi.Tests.Application.CatalogReconciliation;
using Xunit;

namespace InventoryApi.Tests.Controllers;

public class DataQualityControllerTests
{
    [Fact]
    public async Task NayaxCatalogReconciliation_returns_the_reconciled_report_for_products_and_machines()
    {
        var remote = new FakeNayaxCatalogSnapshotProvider
        {
            Products = [new RemoteCatalogEntry(1, "Coke Zero")],
            Machines = []
        };
        var local = new FakeLocalCatalogSnapshotProvider
        {
            Products = [],
            Machines = [new LocalCatalogEntry(10, "Missing Machine")]
        };
        var controller = new DataQualityController(new GetNayaxCatalogReconciliation(remote, local));

        var result = await controller.NayaxCatalogReconciliation(CancellationToken.None);

        var addedProduct = Assert.Single(result.Products);
        Assert.Equal("Added", addedProduct.State);
        var missingMachine = Assert.Single(result.Machines);
        Assert.Equal("MissingRemotely", missingMachine.State);
    }
}
