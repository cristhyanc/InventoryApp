using InventoryApi.Adapters.Nayax;
using InventoryApi.Integrations.Nayax;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Adapters.Nayax;

public class NayaxCatalogSnapshotProviderTests
{
    [Fact]
    public async Task GetProductsAsync_maps_Nayax_products_to_remote_catalog_entries()
    {
        var client = new Mock<INayaxLynxClient>();
        client.Setup(c => c.GetProductsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new NayaxProduct { NayaxProductId = 1, ProductName = "Coke Zero" },
            new NayaxProduct { NayaxProductId = 2, ProductName = null }
        ]);
        var provider = new NayaxCatalogSnapshotProvider(client.Object);

        var products = await provider.GetProductsAsync(CancellationToken.None);

        Assert.Equal(2, products.Count);
        Assert.Contains(products, p => p.ExternalId == 1 && p.Name == "Coke Zero");
        Assert.Contains(products, p => p.ExternalId == 2 && p.Name == string.Empty);
    }

    [Fact]
    public async Task GetMachinesAsync_maps_Nayax_machines_and_preserves_duplicate_identifiers()
    {
        var client = new Mock<INayaxLynxClient>();
        client.Setup(c => c.GetMachinesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new NayaxMachine { MachineID = 10, MachineName = "Machine A" },
            new NayaxMachine { MachineID = 10, MachineName = "Machine A Duplicate" }
        ]);
        var provider = new NayaxCatalogSnapshotProvider(client.Object);

        var machines = await provider.GetMachinesAsync(CancellationToken.None);

        Assert.Equal(2, machines.Count);
        Assert.All(machines, m => Assert.Equal(10, m.ExternalId));
        Assert.Contains(machines, m => m.Name == "Machine A");
        Assert.Contains(machines, m => m.Name == "Machine A Duplicate");
    }
}
