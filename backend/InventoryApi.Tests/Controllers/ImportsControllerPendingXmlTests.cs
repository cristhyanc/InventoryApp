using System.Text.Json;
using Inventory.Application.Imports;
using Inventory.Application.Time;
using InventoryApi.Controllers;
using InventoryApi.Services.Interfaces;
using InventoryApi.Tests.Swagger;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// Locks the HTTP surface of <c>POST api/imports/pending-xml</c> across its move into
/// <see cref="ImportPendingReimbursementXmlFiles"/> (issue #299): the same route, the same
/// <c>200 OK</c> with the same four count keys the Angular client reads, and a controller that
/// now calls the use case directly instead of <c>IImportService</c>.
/// </summary>
public class ImportsControllerPendingXmlTests
{
    [Fact]
    public void The_pending_xml_route_is_unchanged()
    {
        var descriptions = ApiContractTestHost.GetApiDescriptionsFor<ImportsController>();

        Assert.Contains(descriptions, description =>
            string.Equals(description.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase) &&
            description.RelativePath == "api/imports/pending-xml");
    }

    [Fact]
    public async Task The_action_returns_the_use_cases_counts_as_200_OK()
    {
        var source = new Mock<IPendingReimbursementXmlSource>();
        source.Setup(x => x.ListPendingFiles()).Returns(["august.xml"]);
        source.Setup(x => x.ReadAsync("august.xml", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PendingReimbursementXmlFile("august.xml", "HASH-A", [new ImportedReimbursementFacts()]));
        source.Setup(x => x.TryDiscard("august.xml")).Returns(true);
        var store = new Mock<IImportedReimbursementStore>();
        store.Setup(x => x.HasFileWithContentHashAsync("HASH-A", It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var controller = new ImportsController(
            Mock.Of<IImportService>(),
            new ImportPendingReimbursementXmlFiles(source.Object, store.Object, Mock.Of<IClock>()));

        var result = await controller.ImportPendingXmlFiles(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(new ImportedFileImportResult(1, 1, 0, 0), Assert.IsType<ImportedFileImportResult>(ok.Value));
    }

    [Fact]
    public void The_response_keeps_its_four_count_keys()
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(
            new ImportedFileImportResult(2, 7, 1, 3), new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        Assert.Equal(
            ["importedFiles", "importedReimbursements", "skippedFiles", "failedFiles"],
            document.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal(2, document.RootElement.GetProperty("importedFiles").GetInt32());
        Assert.Equal(7, document.RootElement.GetProperty("importedReimbursements").GetInt32());
        Assert.Equal(1, document.RootElement.GetProperty("skippedFiles").GetInt32());
        Assert.Equal(3, document.RootElement.GetProperty("failedFiles").GetInt32());
    }
}
