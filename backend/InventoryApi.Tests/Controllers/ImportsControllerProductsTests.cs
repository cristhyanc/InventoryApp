using Inventory.Application.Imports;
using Inventory.Application.Nayax;
using Inventory.Application.Time;
using InventoryApi.Controllers;
using InventoryApi.Services.Interfaces;
using InventoryApi.Tests.Swagger;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// Locks the HTTP surface of <c>POST api/imports/products</c> across its move into
/// <see cref="ImportNayaxProductCatalog"/> (issue #300): the same route, the same <c>200 OK</c>
/// with the same constant <c>true</c> body, and a controller that now calls the use case directly
/// instead of <c>IImportService</c>.
/// </summary>
public class ImportsControllerProductsTests
{
    [Fact]
    public void The_products_import_route_is_unchanged()
    {
        var descriptions = ApiContractTestHost.GetApiDescriptionsFor<ImportsController>();

        Assert.Contains(descriptions, description =>
            string.Equals(description.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase) &&
            description.RelativePath == "api/imports/products");
    }

    [Fact]
    public async Task The_action_runs_the_use_case_and_returns_200_OK_with_true()
    {
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(client => client.GetProductsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new NayaxProduct { NayaxProductId = 100, ProductName = "Chips", RetailPrice = 3.50m }]);
        nayax.Setup(client => client.GetProductGroupssAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var store = new Mock<INayaxProductCatalogImportStore>();
        var controller = Controller(nayax.Object, store.Object);

        var result = await controller.ImportProducts(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(true, ok.Value);
        store.Verify(
            x => x.ApplyAsync(It.IsAny<NayaxProductCatalogImport>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// The endpoint has no failure response of its own: an upstream Nayax failure propagates to
    /// the API's error handling exactly as it did before the migration, rather than being reported
    /// as a successful import.
    /// </summary>
    [Fact]
    public async Task An_upstream_failure_propagates_instead_of_answering_200_OK()
    {
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(client => client.GetProductsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("upstream unavailable"));
        nayax.Setup(client => client.GetProductGroupssAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var store = new Mock<INayaxProductCatalogImportStore>();
        var controller = Controller(nayax.Object, store.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.ImportProducts(CancellationToken.None));

        store.Verify(
            x => x.ApplyAsync(It.IsAny<NayaxProductCatalogImport>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static ImportsController Controller(INayaxLynxClient nayax, INayaxProductCatalogImportStore store) =>
        new(
            Mock.Of<IImportService>(),
            new ImportNayaxProductCatalog(nayax, store, Mock.Of<IClock>()),
            new ImportPendingReimbursementXmlFiles(
                Mock.Of<IPendingReimbursementXmlSource>(),
                Mock.Of<IImportedReimbursementStore>(),
                Mock.Of<IClock>()));
}
