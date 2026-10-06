using Inventory.Application.Documents;
using Inventory.Application.Purchases;
using Inventory.Domain.Gst;
using InventoryApi.Controllers;
using InventoryApi.DTOs;
using InventoryApi.Tests.Application.Time;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// The HTTP behaviour of <see cref="PurchasesController"/> now that issue #304 pointed it straight
/// at the <see cref="Inventory.Application.Purchases"/> use cases and gave it the API-owned
/// <see cref="PurchaseResponse"/>. These tests drive it through a mocked
/// <see cref="IPurchaseStore"/> - the narrow persistence port - so they pin exactly what the
/// retired <c>PurchaseService</c> delegator used to decide: which use-case result becomes
/// <c>404</c>, which becomes <c>400</c> and with what message, where the <c>201</c> points, and
/// that the total-validation block still travels next to every purchase.
///
/// <c>InventoryApi.Tests.DTOs.PurchaseResponseJsonContractTests</c> covers the serialised bytes;
/// <see cref="PurchasesControllerRouteTests"/> covers the routes.
/// </summary>
public class PurchasesControllerTests
{
    private const string InvalidFileOrSupplier = "Invalid file or supplier";

    private static PurchasesController CreateController(IPurchaseStore store, IDocumentStorage? documents = null)
    {
        var storage = documents ?? NoDocuments().Object;
        return new PurchasesController(
            new ListPurchases(store),
            new GetPurchase(store),
            new GetPurchaseFile(store, storage),
            new UploadPurchase(store, storage, new FakeClock(new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc))),
            new UpdatePurchase(store),
            new DeletePurchase(store, storage),
            new ComputePurchaseTotalValidation(),
            new ComputePurchaseGstSummary());
    }

    private static Mock<IDocumentStorage> NoDocuments()
    {
        var documents = new Mock<IDocumentStorage>();
        documents.Setup(d => d.OpenReadAsync(It.IsAny<DocumentCategory>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DocumentContent?)null);
        return documents;
    }

    [Fact]
    public async Task GetAll_returns_each_purchase_with_its_validation_block()
    {
        var store = new Mock<IPurchaseStore>();
        store.Setup(s => s.ListAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Record(totalAmount: 30m)]);

        var result = await CreateController(store.Object).GetAll(null);

        var envelope = Assert.Single(Assert.IsAssignableFrom<IEnumerable<PurchaseResponseDto>>(
            Assert.IsType<OkObjectResult>(result.Result).Value));
        Assert.Equal(7, envelope.Purchase.Id);
        // Items are 24 at $1.15 = $27.60, plus $5 delivery and $2 package = $34.60 against the
        // entered $30: a mismatch of $4.60.
        Assert.True(envelope.Validation!.HasTotalMismatch);
        Assert.Equal(27.60m, envelope.Validation.CalculatedItemSubtotal);
        Assert.Equal(34.60m, envelope.Validation.CalculatedTotal);
        Assert.Equal(4.60m, envelope.Validation.TotalDifference);
    }

    [Fact]
    public async Task GetAll_passes_the_supplier_filter_through()
    {
        var store = new Mock<IPurchaseStore>();
        store.Setup(s => s.ListAsync(4, It.IsAny<CancellationToken>())).ReturnsAsync([Record()]);

        var result = await CreateController(store.Object).GetAll(4);

        Assert.Single(Assert.IsAssignableFrom<IEnumerable<PurchaseResponseDto>>(
            Assert.IsType<OkObjectResult>(result.Result).Value));
        store.Verify(s => s.ListAsync(4, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Get_returns_not_found_for_an_unknown_purchase()
    {
        var store = new Mock<IPurchaseStore>();
        store.Setup(s => s.FindByIdAsync(404, It.IsAny<CancellationToken>())).ReturnsAsync((PurchaseRecord?)null);

        var result = await CreateController(store.Object).Get(404);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task Get_returns_the_purchase_with_a_matching_total_and_no_mismatch()
    {
        var store = new Mock<IPurchaseStore>();
        store.Setup(s => s.FindByIdAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(Record(totalAmount: 34.60m));

        var result = await CreateController(store.Object).Get(7);

        var envelope = Assert.IsType<PurchaseResponseDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal("Weekly restock", envelope.Purchase.Title);
        Assert.False(envelope.Validation!.HasTotalMismatch);
        Assert.Null(envelope.Validation.TotalDifference);
    }

    /// <summary>
    /// A purchase whose supporting document is gone, and a purchase that does not exist, are the
    /// same <c>404</c>: the use case reports both as <c>null</c>, exactly as before, so a caller
    /// cannot tell the two apart.
    /// </summary>
    [Fact]
    public async Task GetFile_returns_not_found_when_the_document_is_missing()
    {
        var store = new Mock<IPurchaseStore>();
        store.Setup(s => s.FindFileMetadataAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PurchaseFileMetadata("abc-def.jpg", "image/jpeg", "scan.jpg", 3));

        var result = await CreateController(store.Object).GetFile(7);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task GetFile_serves_the_stored_document_under_its_original_name_and_content_type()
    {
        var store = new Mock<IPurchaseStore>();
        store.Setup(s => s.FindFileMetadataAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PurchaseFileMetadata("abc-def.jpg", "image/jpeg", "scan.jpg", 3));
        var documents = new Mock<IDocumentStorage>();
        documents.Setup(d => d.OpenReadAsync(DocumentCategory.PurchaseDocument, "abc-def.jpg", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new DocumentContent(new MemoryStream([1, 2, 3]), 3));

        var result = await CreateController(store.Object, documents.Object).GetFile(7);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal(new byte[] { 1, 2, 3 }, file.FileContents);
        Assert.Equal("image/jpeg", file.ContentType);
        Assert.Equal("scan.jpg", file.FileDownloadName);
    }

    /// <summary>
    /// Model binding supplies no file at all when the multipart body omits it. The controller keeps
    /// answering the retired delegator's deliberately vague message rather than reaching the use
    /// case with a null file.
    /// </summary>
    [Fact]
    public async Task Upload_returns_bad_request_when_no_file_was_posted()
    {
        var store = new Mock<IPurchaseStore>();

        var result = await UploadThrough(CreateController(store.Object), file: null!);

        Assert.Equal(InvalidFileOrSupplier, Assert.IsType<BadRequestObjectResult>(result.Result).Value);
        store.Verify(
            s => s.CreateAsync(
                It.IsAny<PurchaseFields>(),
                It.IsAny<IReadOnlyList<PurchaseItemInput>>(),
                It.IsAny<PurchaseFileMetadata>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("scan")]
    public async Task Upload_returns_bad_request_for_a_document_type_that_is_not_allowed(string fileName)
    {
        var store = new Mock<IPurchaseStore>();

        var result = await UploadThrough(CreateController(store.Object), CreateFile(fileName));

        Assert.Equal(InvalidFileOrSupplier, Assert.IsType<BadRequestObjectResult>(result.Result).Value);
    }

    [Fact]
    public async Task Upload_returns_bad_request_for_an_empty_document()
    {
        var store = new Mock<IPurchaseStore>();

        var result = await UploadThrough(CreateController(store.Object), CreateFile("scan.jpg", []));

        Assert.Equal(InvalidFileOrSupplier, Assert.IsType<BadRequestObjectResult>(result.Result).Value);
    }

    [Fact]
    public async Task Upload_returns_bad_request_with_the_validation_message_for_a_malformed_item()
    {
        var store = new Mock<IPurchaseStore>();
        store.Setup(s => s.SupplierExistsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var result = await UploadThrough(
            CreateController(store.Object),
            CreateFile("scan.jpg"),
            items: """[{"productId":3,"quantity":0,"unitCost":1.15}]""");

        Assert.Equal(
            Inventory.Domain.Purchases.PurchaseItemFormatPolicy.InvalidItemsMessage,
            Assert.IsType<BadRequestObjectResult>(result.Result).Value);
    }

    [Fact]
    public async Task Upload_returns_created_at_the_single_purchase_route_with_its_validation_block()
    {
        var store = new Mock<IPurchaseStore>();
        store.Setup(s => s.SupplierExistsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        store.Setup(s => s.AllProductsExistAsync(It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        store.Setup(s => s.FindConflictingCostTransitionBaselineAsync(
                It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(((long, DateTime)?)null);
        store.Setup(s => s.CreateAsync(
                It.IsAny<PurchaseFields>(),
                It.IsAny<IReadOnlyList<PurchaseItemInput>>(),
                It.IsAny<PurchaseFileMetadata>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Record(totalAmount: 30m));
        var documents = new Mock<IDocumentStorage>();

        var result = await UploadThrough(
            CreateController(store.Object, documents.Object),
            CreateFile("scan.jpg"),
            items: """[{"productId":3,"quantity":24,"unitCost":1.15}]""");

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        Assert.Equal(nameof(PurchasesController.Get), created.ActionName);
        Assert.Equal(7, created.RouteValues!["id"]);
        var envelope = Assert.IsType<PurchaseResponseDto>(created.Value);
        Assert.Equal(7, envelope.Purchase.Id);
        Assert.True(envelope.Validation!.HasTotalMismatch);
        documents.Verify(
            d => d.SaveAsync(DocumentCategory.PurchaseDocument, It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Update_returns_not_found_for_an_unknown_purchase()
    {
        var store = new Mock<IPurchaseStore>();
        store.Setup(s => s.UpdateAsync(404, It.IsAny<PurchaseFields>(), It.IsAny<IReadOnlyList<PurchaseItemInput>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PurchaseRecord?)null);

        var result = await CreateController(store.Object)
            .Update(404, "Updated", null, null, null, null, null, null, null, null, null);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task Update_returns_bad_request_with_the_stores_validation_message()
    {
        var store = new Mock<IPurchaseStore>();
        store.Setup(s => s.UpdateAsync(7, It.IsAny<PurchaseFields>(), It.IsAny<IReadOnlyList<PurchaseItemInput>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("One or more purchase products do not exist."));

        var result = await CreateController(store.Object)
            .Update(7, "Updated", null, null, null, null, null, null, null, null, null);

        Assert.Equal(
            "One or more purchase products do not exist.",
            Assert.IsType<BadRequestObjectResult>(result.Result).Value);
    }

    /// <summary>
    /// Only a literal <c>null</c> items body leaves the stored items untouched; an omitted or blank
    /// field is an empty list that clears them. That distinction was the retired delegator's and is
    /// unchanged.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Update_treats_an_omitted_items_field_as_an_empty_list(string? items)
    {
        var store = new Mock<IPurchaseStore>();
        IReadOnlyList<PurchaseItemInput>? captured = null;
        store.Setup(s => s.UpdateAsync(7, It.IsAny<PurchaseFields>(), It.IsAny<IReadOnlyList<PurchaseItemInput>?>(), It.IsAny<CancellationToken>()))
            .Callback((int _, PurchaseFields _, IReadOnlyList<PurchaseItemInput>? supplied, CancellationToken _) => captured = supplied)
            .ReturnsAsync(Record());

        await CreateController(store.Object).Update(7, "Updated", null, null, null, null, null, null, null, null, items);

        Assert.NotNull(captured);
        Assert.Empty(captured);
    }

    [Fact]
    public async Task Update_leaves_the_stored_items_alone_for_a_null_items_body()
    {
        var store = new Mock<IPurchaseStore>();
        var captured = new List<IReadOnlyList<PurchaseItemInput>?>();
        store.Setup(s => s.UpdateAsync(7, It.IsAny<PurchaseFields>(), It.IsAny<IReadOnlyList<PurchaseItemInput>?>(), It.IsAny<CancellationToken>()))
            .Callback((int _, PurchaseFields _, IReadOnlyList<PurchaseItemInput>? supplied, CancellationToken _) => captured.Add(supplied))
            .ReturnsAsync(Record());

        await CreateController(store.Object).Update(7, "Updated", null, null, null, null, null, null, null, null, "null");

        Assert.Null(Assert.Single(captured));
    }

    [Fact]
    public async Task Delete_maps_the_use_case_result_to_no_content()
    {
        var store = new Mock<IPurchaseStore>();
        store.Setup(s => s.DeleteAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync("abc-def.jpg");
        var documents = new Mock<IDocumentStorage>();

        var result = await CreateController(store.Object, documents.Object).Delete(7);

        Assert.IsType<NoContentResult>(result);
        documents.Verify(
            d => d.DeleteAsync(DocumentCategory.PurchaseDocument, "abc-def.jpg", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Delete_returns_not_found_for_an_unknown_purchase()
    {
        var store = new Mock<IPurchaseStore>();
        store.Setup(s => s.DeleteAsync(404, It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);

        Assert.IsType<NotFoundResult>(await CreateController(store.Object).Delete(404));
    }

    [Fact]
    public async Task Delete_returns_bad_request_with_the_stores_preservation_message()
    {
        var store = new Mock<IPurchaseStore>();
        store.Setup(s => s.DeleteAsync(7, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("This purchase cannot be deleted."));

        var result = await CreateController(store.Object).Delete(7);

        Assert.Equal("This purchase cannot be deleted.", Assert.IsType<BadRequestObjectResult>(result).Value);
    }

    private static Task<ActionResult<PurchaseResponseDto>> UploadThrough(
        PurchasesController controller, IFormFile file, string? items = null) =>
        controller.Upload(file, "Weekly restock", null, 30m, 5m, null, 2m, null, null, 4, items);

    private static IFormFile CreateFile(string fileName, byte[]? bytes = null)
    {
        var content = new MemoryStream(bytes ?? [1, 2, 3]);
        var file = new Mock<IFormFile>();
        file.Setup(f => f.Length).Returns(content.Length);
        file.Setup(f => f.FileName).Returns(fileName);
        file.Setup(f => f.ContentType).Returns("image/jpeg");
        file.Setup(f => f.OpenReadStream()).Returns(content);
        return file.Object;
    }

    private static PurchaseRecord Record(decimal? totalAmount = 30m) => new(
        7,
        BusinessId: 1,
        "Weekly restock",
        null,
        totalAmount,
        5m,
        GstClassification.Unknown,
        GstClassificationSource.Unknown,
        2m,
        GstClassification.Unknown,
        GstClassificationSource.Unknown,
        new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc),
        4,
        new PurchaseSupplierRecord(4, "Acme", null, null, null, null),
        [new PurchaseItemRecord(11, 7, 3, 24m, 1.15m, GstClassification.Unknown, GstClassificationSource.Unknown, Product: null)],
        "scan.jpg",
        "abc-def.jpg",
        "image/jpeg",
        3,
        new DateTime(2026, 3, 1, 10, 5, 0, DateTimeKind.Utc));
}
