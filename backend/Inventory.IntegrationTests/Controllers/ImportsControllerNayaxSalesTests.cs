using System.Text;
using System.Text.Json;
using Inventory.Application.Costing;
using Inventory.Application.Imports;
using Inventory.Application.Time;
using InventoryApi.Controllers;
using InventoryApi.Tests.Swagger;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// Locks the HTTP surface of <c>POST api/imports/nayax-sales</c> across its move into
/// <see cref="ImportNayaxSales"/> (issue #301): the same route, the same <c>200 OK</c> with the
/// three count keys the Angular client reads, the same two <c>400 Bad Request</c> validation
/// messages, and an <c>IFormFile</c> that stays at this boundary - the use case receives only the
/// uploaded name and a way to open the bytes.
/// </summary>
public class ImportsControllerNayaxSalesTests
{
    [Fact]
    public void The_nayax_sales_route_is_unchanged()
    {
        var descriptions = ApiContractTestHost.GetApiDescriptionsFor<ImportsController>();

        Assert.Contains(descriptions, description =>
            string.Equals(description.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase) &&
            description.RelativePath == "api/imports/nayax-sales");
    }

    [Fact]
    public async Task The_action_returns_the_use_cases_counts_as_200_OK()
    {
        var reader = new Mock<INayaxSalesWorkbookReader>();
        reader.Setup(x => x.Read(It.IsAny<Stream>(), "sales.csv"))
            .Returns([Row(1001), Row(1002)]);
        var store = new Mock<INayaxSalesImportStore>();
        store.Setup(x => x.GetProductCandidatesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        store.Setup(x => x.FindByTransactionIdAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CostableSale?)null);
        store.Setup(x => x.Add(It.IsAny<ImportedNayaxSale>()))
            .Returns((ImportedNayaxSale sale) => new CostableSale
            {
                TransactionId = sale.TransactionId,
                AuthorizationTime = sale.MachineAuthorizationTime,
            });
        store.Setup(x => x.GetTransitionCutoffsAsync(It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<long, DateTime>());

        var result = await Controller(reader.Object, store.Object)
            .ImportNayaxSales(Upload("sales.csv"), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(new NayaxSalesImportResult(2, 0, 0), Assert.IsType<NayaxSalesImportResult>(ok.Value));
        store.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task A_missing_file_is_refused_before_the_use_case_runs()
    {
        var reader = new Mock<INayaxSalesWorkbookReader>(MockBehavior.Strict);

        var result = await Controller(reader.Object, Mock.Of<INayaxSalesImportStore>())
            .ImportNayaxSales(null!, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal("An Excel file is required.", badRequest.Value);
    }

    [Fact]
    public async Task An_empty_file_is_refused_before_the_use_case_runs()
    {
        var reader = new Mock<INayaxSalesWorkbookReader>(MockBehavior.Strict);

        var result = await Controller(reader.Object, Mock.Of<INayaxSalesImportStore>())
            .ImportNayaxSales(Upload("sales.csv", string.Empty), CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal("An Excel file is required.", badRequest.Value);
    }

    /// <summary>
    /// The unsupported-format refusal still reaches the caller as the same <c>400</c> with the same
    /// message, now raised by the use case and claimed by this action's own
    /// <c>InvalidOperationException</c> handling rather than by the removed legacy service.
    /// </summary>
    [Fact]
    public async Task An_unsupported_file_format_is_refused_as_400_with_its_message()
    {
        var reader = new Mock<INayaxSalesWorkbookReader>(MockBehavior.Strict);

        var result = await Controller(reader.Object, Mock.Of<INayaxSalesImportStore>())
            .ImportNayaxSales(Upload("sales.txt"), CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal("Only .xlsx, .xls, or .csv files are supported.", badRequest.Value);
    }

    [Fact]
    public void The_response_keeps_its_three_count_keys()
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(
            new NayaxSalesImportResult(4, 2, 1), new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        Assert.Equal(
            ["imported", "updated", "skipped"],
            document.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal(4, document.RootElement.GetProperty("imported").GetInt32());
        Assert.Equal(2, document.RootElement.GetProperty("updated").GetInt32());
        Assert.Equal(1, document.RootElement.GetProperty("skipped").GetInt32());
    }

    private static NayaxSalesImportRow Row(long transactionId) =>
        new(transactionId, 1, new DateTime(2026, 9, 2, 14, 30, 0), 55, 10, "Machine", 3m, "Card", "Snack", null);

    private static IFormFile Upload(string fileName, string content = "TransactionID\n1001")
    {
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        return new FormFile(stream, 0, stream.Length, "file", fileName);
    }

    private static ImportsController Controller(INayaxSalesWorkbookReader reader, INayaxSalesImportStore store) =>
        new(
            new ImportNayaxProductCatalog(
                Mock.Of<Inventory.Application.Nayax.INayaxLynxClient>(),
                Mock.Of<INayaxProductCatalogImportStore>(),
                Mock.Of<IClock>()),
            new ImportNayaxSales(reader, store, Mock.Of<ICostSale>(), Mock.Of<IRebuildProductCost>()),
            new ImportPendingReimbursementXmlFiles(
                Mock.Of<IPendingReimbursementXmlSource>(),
                Mock.Of<IImportedReimbursementStore>(),
                Mock.Of<IClock>()));
}
