using InventoryApi.Controllers;
using InventoryApi.Data;
using InventoryApi.DTOs;
using Inventory.Application.Nayax;
using Inventory.Application.Products;
using Inventory.Application.Purchases;
using Inventory.Application.Reorder;
using Inventory.Application.Reporting.Dashboard;
using InventoryApi.Adapters.Persistence;
using InventoryApi.Models;
using InventoryApi.Services;
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

    private static ProductsController CreateController(AppDbContext db)
    {
        var nayaxMock = new Mock<INayaxLynxClient>();
        var calculateReorderNeeds = new CalculateReorderNeeds(nayaxMock.Object, new EfOutstandingSupplierOrderQuantityStore(db));
        var getInventoryValuationSummary = new GetInventoryValuationSummary(new EfInventoryValuationFactsProvider(db));
        var getProductPriceComparison = new GetProductPriceComparison(new EfProductPurchasePriceHistoryProvider(db));
        var store = new EfProductStore(db, TestCostingUseCases.Rebuild(db));
        var catalog = new EfProductCatalogStore(db);
        var listLowStockProducts = new ListLowStockProducts(catalog, calculateReorderNeeds);
        var productService = new ProductService(
            new ListProducts(catalog, listLowStockProducts),
            new GetProduct(catalog),
            listLowStockProducts,
            new CreateProduct(store),
            new UpdateProduct(store),
            new DeleteProduct(store));
        return new ProductsController(productService, getInventoryValuationSummary, getProductPriceComparison);
    }

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
}
