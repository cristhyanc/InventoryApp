using InventoryApi.Controllers;
using Inventory.Infrastructure.Data;
using InventoryApi.DTOs;
using Inventory.Application.Nayax;
using Inventory.Application.Products;
using Inventory.Application.Purchases;
using Inventory.Application.Reorder;
using Inventory.Application.Reporting.Dashboard;
using Inventory.Domain.Gst;
using Inventory.Infrastructure.Persistence;
using Inventory.Infrastructure.Reporting.Persistence;
using Inventory.Infrastructure.Models;
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
        var calculateReorderNeeds = new CalculateReorderNeeds(nayaxMock.Object, new EfOutstandingSupplierOrderQuantityStore(db));
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
            new SetProductGstRule(store));
    }

    private static ProductResponse SingleProduct(ActionResult<IEnumerable<ProductResponse>> result) =>
        Assert.Single(Assert.IsAssignableFrom<IEnumerable<ProductResponse>>(
            Assert.IsType<OkObjectResult>(result.Result).Value));

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

        var product = SingleProduct(await CreateController(db).GetAll(null, null, null, null, CancellationToken.None));

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

    [Fact]
    public async Task Delete_ExistingProduct_ReturnsNoContent()
    {
        using var db = CreateDbContext();
        db.Products.Add(new Product { Id = 1, Name = "Coke" });
        await db.SaveChangesAsync();

        Assert.IsType<NoContentResult>(await CreateController(db).Delete(1));
    }

    [Fact]
    public async Task Delete_NonExistentProduct_ReturnsNotFound()
    {
        using var db = CreateDbContext();

        Assert.IsType<NotFoundResult>(await CreateController(db).Delete(999));
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
            SingleProduct(await CreateController(db).GetAll(null, null, null, null, CancellationToken.None)),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        Assert.DoesNotContain("gstRule", json, StringComparison.OrdinalIgnoreCase);
    }
}
