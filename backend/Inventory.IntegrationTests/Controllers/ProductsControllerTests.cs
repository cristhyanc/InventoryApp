using InventoryApi.Controllers;
using Inventory.Infrastructure.Data;
using InventoryApi.DTOs;
using Inventory.Application.Access;
using Inventory.Application.Nayax;
using Inventory.Application.Products;
using Inventory.Application.Purchases;
using Inventory.Application.Reorder;
using Inventory.Application.Reporting.Dashboard;
using Inventory.Domain.Gst;
using Inventory.Infrastructure.Persistence;
using Inventory.Infrastructure.Reporting.Persistence;
using Inventory.Infrastructure.Models;
using InventoryApi.Tests.Application.Access;
using InventoryApi.Tests.Application.Gst;
using InventoryApi.Tests.Application.Nayax;
using InventoryApi.Tests.Application.SupportSessions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Controllers;

public class ProductsControllerTests
{
    private static AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return TestAppDbContext.Unrestricted(options);
    }

    /// <summary>
    /// <paramref name="productStore"/> overrides the real <c>EfProductStore</c> only for the
    /// error-path tests that need a write to fail; every other test exercises the EF adapter.
    /// </summary>
    private static ProductsController CreateController(AppDbContext db, IProductStore? productStore = null)
    {
        var nayaxMock = new Mock<INayaxLynxClient>();
        // No machine fleet: the reorder-alert listing then accounts for storage stock and
        // outstanding supplier orders only, which is what these tests exercise.
        nayaxMock.Setup(client => client.GetMachinesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachine>());
        var calculateReorderNeeds = new CalculateReorderNeeds(
            nayaxMock.Object,
            new EfOutstandingSupplierOrderQuantityStore(db),
            FakeNayaxProductLinkStore.EveryIdLinkedToItself());
        var getInventoryValuationSummary = new GetInventoryValuationSummary(new EfInventoryValuationFactsProvider(db));
        var getProductPriceComparison = new GetProductPriceComparison(new EfProductPurchasePriceHistoryProvider(db));
        var store = productStore ?? new EfProductStore(db, TestCostingUseCases.Rebuild(db));
        var catalog = new EfProductCatalogStore(db);
        var listLowStockProducts = new ListLowStockProducts(catalog, calculateReorderNeeds);
        return new ProductsController(
            new ListProducts(catalog, listLowStockProducts),
            new GetProduct(catalog),
            listLowStockProducts,
            new CreateProduct(store),
            new UpdateProduct(store),
            new DeleteProduct(store),
            getInventoryValuationSummary,
            getProductPriceComparison,
            new GetProductGstRule(store),
            new SetProductGstRule(store),
            // An Owner, so the catalogue actions answer the full response; the role-dependent shape
            // itself is MachineAndProductFinancialRedactionApiTests, through the real pipeline.
            new CurrentCapabilities(
                new FakeSupportSessionAccessor(),
                new FakeCurrentBusinessProvider(businessId: 1),
                new FakeRolePreviewAccessor()))
        {
            // The catalogue actions publish the reorder-listing metadata headers (issue #726), so a
            // directly constructed controller needs a response to write them to. The headers
            // themselves are asserted through the real pipeline in
            // InventoryApi.Tests.Controllers.LowStockReorderMetadataApiTests.
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    private static ProductResponse SingleProduct(ActionResult<IEnumerable<ProductResponse>> result) =>
        Assert.Single(Products(result));

    private static IEnumerable<ProductResponse> Products(ActionResult<IEnumerable<ProductResponse>> result) =>
        Assert.IsAssignableFrom<IEnumerable<ProductResponse>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);

    private static ProductUpdateDto UpdateDto(int lowStockThreshold, int restockTo) =>
        new("Coke", null, null, 1m, lowStockThreshold, restockTo, "unit", null, null, true);

    [Fact]
    public async Task Update_RestockToBelowThreshold_ReturnsBadRequestWithMessage()
    {
        using var db = CreateDbContext();
        db.Products.Add(new Product { Id = 1, Name = "Coke", LowStockThreshold = 57, RestockTo = 100 });
        await db.SaveChangesAsync();

        var result = await CreateController(db).Update(1, UpdateDto(57, 40));

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("Restock To must be greater than or equal to the Low Stock Threshold.", badRequest.Value);
    }

    [Fact]
    public async Task Update_NegativeLowStockThreshold_ReturnsBadRequest()
    {
        using var db = CreateDbContext();
        db.Products.Add(new Product { Id = 1, Name = "Coke", LowStockThreshold = 5, RestockTo = 10 });
        await db.SaveChangesAsync();

        var result = await CreateController(db).Update(1, UpdateDto(-1, 10));

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Update_NegativeRestockTo_ReturnsBadRequest()
    {
        using var db = CreateDbContext();
        db.Products.Add(new Product { Id = 1, Name = "Coke", LowStockThreshold = 5, RestockTo = 10 });
        await db.SaveChangesAsync();

        var result = await CreateController(db).Update(1, UpdateDto(5, -1));

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Update_NonExistentProduct_ReturnsNotFound_EvenWithInvalidRestockSettings()
    {
        using var db = CreateDbContext();

        var result = await CreateController(db).Update(999, UpdateDto(57, 40));

        Assert.IsType<NotFoundResult>(result);
    }

    /// <summary>
    /// A product store whose write fails the way infrastructure does: it reports the product exists,
    /// then throws from the update. <paramref name="failure"/> is deliberately an
    /// <see cref="InvalidOperationException"/> - the exact type the retired delegator used to signal
    /// *validation* with, and therefore the one the removed broad catch in
    /// <c>ProductsController.Update</c> would have converted into a 400.
    /// </summary>
    private static Mock<IProductStore> StoreThatFailsTheUpdate(Exception failure)
    {
        var store = new Mock<IProductStore>();
        store.Setup(s => s.ExistsAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        store.Setup(s => s.UpdateAsync(1, It.IsAny<ProductUpdateFields>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);
        return store;
    }

    /// <summary>
    /// Issue #303's one deliberate, human-approved error-path change (approved by the repository owner
    /// in the review of PR #355): <c>Update</c> no longer wraps the call in a broad
    /// <c>catch (InvalidOperationException)</c>, so an unexpected failure from below the use case is
    /// no longer reported to the client as a 400 carrying that exception's internal message. It
    /// propagates uncaught to <see cref="InventoryApi.Http.GlobalExceptionHandler"/>, which logs it
    /// once and answers a generic 500 with no exception message -
    /// <c>InventoryApi.Tests.Http.GlobalExceptionHandlerTests</c> pins that half for exactly this
    /// exception type. This is the same shape issue #59 gave <c>StockController</c>, whose
    /// propagation tests read the same way.
    /// </summary>
    [Fact]
    public async Task Update_UnexpectedStoreFailure_PropagatesUncaught_InsteadOfBecomingABadRequest()
    {
        using var db = CreateDbContext();
        var failure = new InvalidOperationException("The connection pool was exhausted.");

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateController(db, StoreThatFailsTheUpdate(failure).Object).Update(1, UpdateDto(5, 10)));

        Assert.Same(failure, thrown);
    }

    /// <summary>
    /// The other half of the same change: the narrowing is confined to the unexpected failure. An
    /// invalid request is still answered by the use case's outcome as a 400 with the unchanged
    /// message, decided before the store is written to at all, so a store that would throw never
    /// gets the chance to.
    /// </summary>
    [Fact]
    public async Task Update_InvalidRestockSettings_StillReturnsBadRequest_WithoutWritingToTheStore()
    {
        using var db = CreateDbContext();
        var store = StoreThatFailsTheUpdate(new InvalidOperationException("The connection pool was exhausted."));

        var result = await CreateController(db, store.Object).Update(1, UpdateDto(57, 40));

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("Restock To must be greater than or equal to the Low Stock Threshold.", badRequest.Value);
        store.Verify(
            s => s.UpdateAsync(It.IsAny<long>(), It.IsAny<ProductUpdateFields>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// Issue #303: the catalogue endpoints answer the API-owned <see cref="ProductResponse"/>, not the
    /// EF entity, and every value a client reads off it - including the derived reorder values - is
    /// unchanged. The wire shape itself is pinned by
    /// <c>InventoryApi.Tests.DTOs.ProductJsonContractTests</c>.
    /// </summary>
    [Fact]
    public async Task GetAll_ReturnsTheApiOwnedResponse_WithTheSameValuesTheEntityCarried()
    {
        using var db = CreateDbContext();
        db.Categories.Add(new Category { Id = 3, Name = "Drinks", Description = "Cold" });
        db.Products.Add(new Product
        {
            Id = 1,
            Name = "Coke",
            Sku = "SKU-1",
            UnitPrice = 3.50m,
            QuantityInStock = 4,
            LowStockThreshold = 10,
            RestockTo = 20,
            CategoryId = 3,
        });
        await db.SaveChangesAsync();

        var product = SingleProduct(await CreateController(db).GetAll(null, null, null, null, null, CancellationToken.None));

        Assert.Equal(1, product.Id);
        Assert.Equal("Coke", product.Name);
        Assert.Equal("SKU-1", product.Sku);
        Assert.Equal(3.50m, product.UnitPrice);
        Assert.Equal("Drinks", product.Category!.Name);
        Assert.Equal(4, product.QuantityInStock);
        // No live machine need or outstanding order is resolved on this path, so the reorder values
        // follow from the persisted fields alone, exactly as before.
        Assert.Equal(16m, product.NeedToOrder);
        Assert.True(product.IsLowStock);
        Assert.True(product.IsReorderAlert);
    }

    [Fact]
    public async Task Get_ExistingProduct_ReturnsTheApiOwnedResponse()
    {
        using var db = CreateDbContext();
        db.Products.Add(new Product { Id = 1, Name = "Coke", QuantityInStock = 40, LowStockThreshold = 5 });
        await db.SaveChangesAsync();

        var result = await CreateController(db).Get(1);

        var product = Assert.IsType<ProductResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal("Coke", product.Name);
        Assert.False(product.IsLowStock);
    }

    [Fact]
    public async Task Get_NonExistentProduct_ReturnsNotFound()
    {
        using var db = CreateDbContext();

        var result = await CreateController(db).Get(999);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    /// <summary>
    /// The reorder-alert listing keeps resolving the live outstanding supplier-order quantity and
    /// reporting it on the response, so the alert a caller sees still accounts for stock already on
    /// order (AGENTS.md § Inventory and historical costing invariants).
    /// </summary>
    [Fact]
    public async Task LowStock_ReturnsTheAlertingProducts_WithTheirResolvedOutstandingOrderQuantity()
    {
        using var db = CreateDbContext();
        db.Products.AddRange(
            new Product { Id = 1, Name = "Party Mix", QuantityInStock = 2, LowStockThreshold = 10, RestockTo = 17 },
            new Product { Id = 2, Name = "Stocked", QuantityInStock = 50, LowStockThreshold = 10, RestockTo = 60 });
        db.SupplierOrders.Add(new SupplierOrder
        {
            SupplierId = 1,
            Lines = { new SupplierOrderLine { ProductId = 1, QuantityOrdered = 8 } },
        });
        await db.SaveChangesAsync();

        var product = SingleProduct(await CreateController(db).LowStock(null, null, null, CancellationToken.None));

        Assert.Equal("Party Mix", product.Name);
        Assert.Equal(8m, product.OnOrderQuantity);
        Assert.Equal(7m, product.NeedToOrder);
        Assert.True(product.IsReorderAlert);
    }

    [Fact]
    public async Task Update_ValidRestockSettings_ReturnsNoContentAndPersistsTheChange()
    {
        using var db = CreateDbContext();
        db.Products.Add(new Product { Id = 1, Name = "Coke", LowStockThreshold = 5, RestockTo = 10 });
        await db.SaveChangesAsync();

        var result = await CreateController(db).Update(1, UpdateDto(6, 12));

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(12, (await db.Products.FindAsync(1L))!.RestockTo);
    }

    /// <summary>
    /// The create route issue #736 added (parent #329 decision P2): a business with no sales
    /// provider creates its own product, and the response is the created product under its new id,
    /// in the same shape <c>GET</c> answers.
    /// </summary>
    [Fact]
    public async Task Create_PersistsTheProduct_AndAnswersCreatedWithIt()
    {
        using var db = CreateDbContext();
        db.Categories.Add(new Category { Id = 3, Name = "Drinks" });
        await db.SaveChangesAsync();

        var result = await CreateController(db).Create(
            new ProductCreateDto("Coke", "SKU-1", "A can", 3.50m, 0, 5, 10, "can", 3, null, true),
            CancellationToken.None);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var product = Assert.IsType<ProductResponse>(created.Value);
        Assert.Equal(("Coke", 3.50m, (long?)3), (product.Name, product.UnitPrice, product.CategoryId));
        Assert.Equal(nameof(ProductsController.Get), created.ActionName);
        Assert.Equal(product.Id, (await db.Products.SingleAsync()).Id);
    }

    [Fact]
    public async Task Create_WithNoName_ReturnsBadRequestAndPersistsNothing()
    {
        using var db = CreateDbContext();

        var result = await CreateController(db).Create(
            new ProductCreateDto("  ", null, null, 1m, 0, 5, 10, "unit", null, null, true),
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal(Inventory.Domain.Products.ProductCataloguePolicy.NameRequiredMessage, badRequest.Value);
        Assert.Empty(await db.Products.ToListAsync());
    }

    [Fact]
    public async Task Create_WithANegativePrice_ReturnsBadRequest()
    {
        using var db = CreateDbContext();

        var result = await CreateController(db).Create(
            new ProductCreateDto("Coke", null, null, -1m, 0, 5, 10, "unit", null, null, true),
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal(
            Inventory.Domain.Products.ProductCataloguePolicy.NegativeUnitPriceMessage,
            badRequest.Value);
    }

    /// <summary>
    /// The three fields the update route started accepting in issue #736. The refusals a
    /// provider-linked product gets are proved against the real relational adapter in
    /// <c>InventoryApi.Tests.Adapters.Persistence.EfProductCatalogueWriteTests</c>.
    /// </summary>
    [Fact]
    public async Task Update_PersistsTheNameThePriceAndTheCategory()
    {
        using var db = CreateDbContext();
        db.Categories.Add(new Category { Id = 3, Name = "Drinks" });
        db.Products.Add(new Product { Id = 1, Name = "Coke", UnitPrice = 1m, LowStockThreshold = 5, RestockTo = 10 });
        await db.SaveChangesAsync();

        var result = await CreateController(db).Update(
            1, new ProductUpdateDto("Coke Zero", null, null, 4.25m, 5, 10, "can", 3, null, true));

        Assert.IsType<NoContentResult>(result);
        var product = await db.Products.FindAsync(1L);
        Assert.Equal(("Coke Zero", 4.25m, (long?)3), (product!.Name, product.UnitPrice, product.CategoryId));
    }

    [Fact]
    public async Task Update_WithNoName_ReturnsBadRequestAndChangesNothing()
    {
        using var db = CreateDbContext();
        db.Products.Add(new Product { Id = 1, Name = "Coke", LowStockThreshold = 5, RestockTo = 10 });
        await db.SaveChangesAsync();

        var result = await CreateController(db).Update(
            1, new ProductUpdateDto(null!, null, null, 1m, 5, 10, "unit", null, null, true));

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("Coke", (await db.Products.FindAsync(1L))!.Name);
    }

    /// <summary>
    /// The catalogue response says which fields the business's sales provider manages, so the UI can
    /// lock them: nothing for a product this business maintains itself, the name and the category
    /// for one the provider named.
    /// </summary>
    [Fact]
    public async Task GetAll_DeclaresWhichFieldsTheProviderManages()
    {
        using var db = CreateDbContext();
        db.Products.Add(new Product { Id = 1, Name = "Local", ExternalProductId = null });
        await db.SaveChangesAsync();

        var local = SingleProduct(await CreateController(db).GetAll(null, null, null, null, null, CancellationToken.None));
        Assert.Empty(local.ProviderManagedFields!);

        db.Products.Single().ExternalProductId = "100";
        await db.SaveChangesAsync();

        var linked = SingleProduct(await CreateController(db).GetAll(null, null, null, null, null, CancellationToken.None));
        Assert.Equal(new[] { "name", "categoryId" }, linked.ProviderManagedFields!);
    }

    /// <summary>
    /// Issue #737's criterion at the API boundary: the catalogue listing says whether each product
    /// may be deleted, so the screen offers <c>Delete</c> only where the server would allow it. A
    /// product with history is refused with the very message the delete would answer <c>409</c>
    /// with, and the single-product read answers the same way.
    /// </summary>
    [Fact]
    public async Task GetAll_PublishesWhetherEachProductMayBeDeleted()
    {
        using var db = CreateDbContext();
        db.Products.AddRange(
            new Product { Id = 1, Name = "Deletable" },
            new Product { Id = 2, Name = "Used" });
        db.StockAdjustments.Add(new StockAdjustment
        {
            ProductId = 2,
            QuantityChange = 4,
            QuantityAfter = 4,
            Reason = StockAdjustmentReason.Restock,
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var products = Products(await controller.GetAll(null, null, null, null, null, CancellationToken.None))
            .ToDictionary(product => product.Name);

        Assert.True(products["Deletable"].CanDelete);
        Assert.Null(products["Deletable"].DeletionRefusal);
        Assert.False(products["Used"].CanDelete);
        Assert.Contains(
            "1 stock movement", products["Used"].DeletionRefusal!, StringComparison.Ordinal);

        var one = Assert.IsType<ProductResponse>(
            Assert.IsType<OkObjectResult>((await controller.Get(2)).Result).Value);
        Assert.False(one.CanDelete);
        Assert.Equal(products["Used"].DeletionRefusal, one.DeletionRefusal);
    }

    /// <summary>
    /// The refusal the listing publishes is the one the delete gives, not a second wording of the
    /// same rule: a client can show it verbatim, and the two cannot drift apart.
    /// </summary>
    [Fact]
    public async Task GetAll_PublishesTheRefusalTheDeleteItselfAnswersWith()
    {
        using var db = CreateDbContext();
        db.Products.Add(new Product { Id = 1, Name = "Used" });
        db.StockAdjustments.Add(new StockAdjustment
        {
            ProductId = 1,
            QuantityChange = 4,
            QuantityAfter = 4,
            Reason = StockAdjustmentReason.Restock,
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var published = SingleProduct(
            await controller.GetAll(null, null, null, null, null, CancellationToken.None)).DeletionRefusal;
        var refused = Assert.IsType<ConflictObjectResult>(await controller.Delete(1, CancellationToken.None));

        Assert.Equal(refused.Value, published);
    }

    /// <summary>
    /// Issue #736's retirement criterion at the API boundary: a deactivated product is hidden from
    /// the catalogue listing, which is the list every product dropdown is built from, and
    /// <c>includeRetired=true</c> is the one explicit way to ask for every product the business owns.
    /// The reorder alerts and the Pick List already refuse a retired product through their own
    /// Domain rules.
    /// </summary>
    [Fact]
    public async Task GetAll_LeavesARetiredProductOutOfTheListing_UnlessItIsAskedFor()
    {
        using var db = CreateDbContext();
        db.Products.AddRange(
            new Product { Id = 1, Name = "Coke", IsActive = true },
            new Product { Id = 2, Name = "Discontinued", IsActive = false });
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var working = Products(await controller.GetAll(null, null, null, null, null, CancellationToken.None));
        var whole = Products(await controller.GetAll(null, null, null, null, true, CancellationToken.None));

        Assert.Equal(["Coke"], working.Select(product => product.Name));
        Assert.Equal(["Coke", "Discontinued"], whole.Select(product => product.Name).Order());
    }

    /// <summary>
    /// The other half of the same criterion - the history a retired product keeps stays reachable.
    /// Reading one by id is unchanged by retirement, which is what lets a client show it, read its
    /// stock history and reactivate it.
    /// </summary>
    [Fact]
    public async Task Get_AnswersARetiredProduct()
    {
        using var db = CreateDbContext();
        db.Products.Add(new Product { Id = 1, Name = "Discontinued", IsActive = false });
        await db.SaveChangesAsync();

        var result = await CreateController(db).Get(1);

        var product = Assert.IsType<ProductResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal("Discontinued", product.Name);
        Assert.False(product.IsActive);
    }

    /// <summary>
    /// Reactivation, end to end over the API's own actions: a retired product is out of the listing,
    /// the explicit listing is how it is found again, and setting <c>isActive</c> back puts it in the
    /// working catalogue with nothing else changed.
    /// </summary>
    [Fact]
    public async Task Update_ReactivatesARetiredProduct_AndItReturnsToTheListing()
    {
        using var db = CreateDbContext();
        db.Products.Add(new Product
        {
            Id = 1,
            Name = "Discontinued",
            QuantityInStock = 7,
            LowStockThreshold = 5,
            RestockTo = 10,
            IsActive = false,
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        Assert.Empty(Products(await controller.GetAll(null, null, null, null, null, CancellationToken.None)));
        var retired = SingleProduct(
            await controller.GetAll(null, null, null, null, true, CancellationToken.None));
        Assert.False(retired.IsActive);

        Assert.IsType<NoContentResult>(await controller.Update(
            1, new ProductUpdateDto("Discontinued", null, null, 1m, 5, 10, "unit", null, null, true)));

        var reactivated = SingleProduct(
            await controller.GetAll(null, null, null, null, null, CancellationToken.None));
        Assert.True(reactivated.IsActive);
        Assert.Equal(7, reactivated.QuantityInStock);
    }

    /// <summary>
    /// A product with history is refused with <c>409</c> and the message naming what refers to it -
    /// never a <c>204</c> that erased a stock movement through the cascade (issue #736).
    /// </summary>
    [Fact]
    public async Task Delete_ProductWithHistory_ReturnsConflictWithTheRefusal()
    {
        using var db = CreateDbContext();
        db.Products.Add(new Product { Id = 1, Name = "Coke" });
        await db.SaveChangesAsync();
        db.StockAdjustments.Add(new StockAdjustment
        {
            ProductId = 1,
            QuantityChange = 4,
            QuantityAfter = 4,
            Reason = StockAdjustmentReason.Restock,
        });
        await db.SaveChangesAsync();

        var result = await CreateController(db).Delete(1, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Contains("1 stock movement", (string)conflict.Value!, StringComparison.Ordinal);
        Assert.True(await db.Products.AnyAsync(product => product.Id == 1));
        Assert.Single(db.StockAdjustments);
    }

    [Fact]
    public async Task Delete_ExistingProduct_ReturnsNoContent()
    {
        using var db = CreateDbContext();
        db.Products.Add(new Product { Id = 1, Name = "Coke" });
        await db.SaveChangesAsync();

        Assert.IsType<NoContentResult>(await CreateController(db).Delete(1, CancellationToken.None));
    }

    [Fact]
    public async Task Delete_NonExistentProduct_ReturnsNotFound()
    {
        using var db = CreateDbContext();

        Assert.IsType<NotFoundResult>(await CreateController(db).Delete(999, CancellationToken.None));
    }

    [Fact]
    public async Task InventoryValueSummary_ReturnsTheAuthoritativeTotal_WhenEveryProductHasKnownCost()
    {
        using var db = CreateDbContext();
        db.Products.AddRange(
            new Product { Id = 1, Name = "Coke", UnitPrice = 5m, InventoryValue = 40m },
            new Product { Id = 2, Name = "Chips", UnitPrice = 3m, InventoryValue = 60m });
        await db.SaveChangesAsync();

        var result = await CreateController(db).InventoryValueSummary(CancellationToken.None);

        Assert.Equal(100m, result.TotalInventoryValue);
        Assert.True(result.IsComplete);
        Assert.Equal(0, result.ProductsWithUnknownCost);
    }

    [Fact]
    public async Task InventoryValueSummary_IsUnavailable_WhenAnyProductHasUnknownCost()
    {
        using var db = CreateDbContext();
        db.Products.AddRange(
            new Product { Id = 1, Name = "Coke", UnitPrice = 5m, InventoryValue = 40m },
            new Product { Id = 2, Name = "Never rebuilt", UnitPrice = 3m, InventoryValue = null });
        await db.SaveChangesAsync();

        var result = await CreateController(db).InventoryValueSummary(CancellationToken.None);

        Assert.Null(result.TotalInventoryValue);
        Assert.False(result.IsComplete);
        Assert.Equal(1, result.ProductsWithUnknownCost);
    }

    [Fact]
    public async Task PriceHistory_NonExistentProduct_ReturnsNotFound()
    {
        using var db = CreateDbContext();

        var result = await CreateController(db).PriceHistory(999, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task PriceHistory_ExistingProductWithHistory_ReturnsTheComparison()
    {
        using var db = CreateDbContext();
        db.Products.Add(new Product { Id = 1, Name = "Coke" });
        db.Suppliers.Add(new Supplier { Id = 1, Name = "Acme Supplies" });
        db.Receipts.Add(new Purchase
        {
            Id = 10,
            Title = "January order",
            SupplierId = 1,
            PurchaseDate = new DateTime(2026, 1, 5),
            Items = { new PurchaseItem { Id = 1, ProductId = 1, UnitCost = 2.00m, Quantity = 10 } }
        });
        await db.SaveChangesAsync();

        var result = await CreateController(db).PriceHistory(1, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<ProductPriceComparisonDto>(ok.Value);
        Assert.Equal(2.00m, dto.Lowest!.UnitCost);
        Assert.Equal("Acme Supplies", dto.Lowest.SupplierName);
        Assert.Single(dto.HistoryNewestFirst);
    }

    /// <summary>
    /// The product GST rule resource (issue #430): a product nobody configured has no rule, a rule
    /// round-trips through the real EF adapter, and setting it leaves every catalogue and costing
    /// value on the product alone - a rule is accounting configuration, never a cost (AGENTS.md
    /// § Purchase GST classification).
    /// </summary>
    [Fact]
    public async Task GstRule_RoundTripsAndLeavesTheProductsCatalogueAndCostingUntouched()
    {
        using var db = CreateDbContext();
        db.Products.Add(new Product
        {
            Id = 1,
            Name = "Coke",
            UnitPrice = 3.50m,
            AverageUnitCost = 1.10m,
            CostingQuantity = 12,
            InventoryValue = 13.20m,
            QuantityInStock = 12,
            UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var initial = Assert.IsType<ProductGstRuleResponse>(
            Assert.IsType<OkObjectResult>((await controller.GetGstRule(1, CancellationToken.None)).Result).Value);
        Assert.Equal((1L, GstRules.None), (initial.ProductId, initial.GstRule));

        var saved = await controller.SetGstRule(
            1, new ProductGstRuleDto(GstClassification.Taxable), CancellationToken.None);

        Assert.IsType<NoContentResult>(saved);
        var updated = Assert.IsType<ProductGstRuleResponse>(
            Assert.IsType<OkObjectResult>((await controller.GetGstRule(1, CancellationToken.None)).Result).Value);
        Assert.Equal(GstClassification.Taxable, updated.GstRule);

        var product = await db.Products.AsNoTracking().SingleAsync(p => p.Id == 1);
        Assert.Equal(
            (3.50m, 1.10m, 12, 13.20m, 12, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            (product.UnitPrice, product.AverageUnitCost, product.CostingQuantity, product.InventoryValue,
                product.QuantityInStock, product.UpdatedAt));
        Assert.Empty(db.StockAdjustments.AsNoTracking());
    }

    [Fact]
    public async Task GstRule_NonExistentProduct_ReturnsNotFound()
    {
        using var db = CreateDbContext();
        var controller = CreateController(db);

        Assert.IsType<NotFoundResult>((await controller.GetGstRule(999, CancellationToken.None)).Result);
        Assert.IsType<NotFoundResult>(await controller.SetGstRule(
            999, new ProductGstRuleDto(GstClassification.Taxable), CancellationToken.None));
    }

    /// <summary>
    /// An undefined rule value is answered <c>400</c> with the vocabulary's own message and leaves
    /// the rule the product already had in place.
    /// </summary>
    [Fact]
    public async Task GstRule_UndefinedValue_ReturnsBadRequestAndChangesNothing()
    {
        using var db = CreateDbContext();
        db.Products.Add(new Product { Id = 1, Name = "Coke", GstRule = GstClassification.GstFree });
        await db.SaveChangesAsync();
        var controller = CreateController(db);

        var result = await controller.SetGstRule(
            1, new ProductGstRuleDto((GstClassification)999), CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(GstRules.UnsupportedRuleMessage, badRequest.Value);
        Assert.Equal(
            GstClassification.GstFree,
            (await db.Products.AsNoTracking().SingleAsync(p => p.Id == 1)).GstRule);
    }

    /// <summary>
    /// The rule is deliberately absent from the catalogue payload: that payload is a pinned API
    /// contract, so the rule is published on its own resource instead (see
    /// <c>Inventory.Infrastructure.Models.Product.GstRule</c>). This pins that decision, so adding
    /// the field to the catalogue response has to be a conscious change with its own contract tests.
    /// </summary>
    [Fact]
    public async Task GstRule_IsNotCarriedOnTheCatalogueResponse()
    {
        using var db = CreateDbContext();
        db.Products.Add(new Product { Id = 1, Name = "Coke", GstRule = GstClassification.Taxable });
        await db.SaveChangesAsync();

        var json = System.Text.Json.JsonSerializer.Serialize(
            SingleProduct(await CreateController(db).GetAll(null, null, null, null, null, CancellationToken.None)),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        Assert.DoesNotContain("gstRule", json, StringComparison.OrdinalIgnoreCase);
    }
}
