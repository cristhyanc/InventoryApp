using InventoryApi.Data;
using Inventory.Application.Nayax;
using InventoryApi.Models;
using InventoryApi.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Services;

public class ImportServiceTests
{
    [Fact]
    public async Task ImportProducts_adds_new_products_and_categories()
    {
        await using var db = CreateDbContext();
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(client => client.GetProductsAsync(It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(new List<NayaxProduct>
            {
                new() { NayaxProductId = 100, ProductName = "X", ProductGroupId = 10, ProductCostPrice = 2 }
            });
        nayax.Setup(client => client.GetProductGroupssAsync(It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(new List<NayaxProductGroup>
            {
                new() { ProductGroupID = 10, ProductGroupName = "G1" }
            });

        var result = await CreateImportService(db, nayax.Object).ImportProductsAsync();

        Assert.True(result);
        Assert.NotNull(await db.Products.FindAsync(100L));
        Assert.NotNull(await db.Categories.FindAsync(10L));
    }

    [Fact]
    public async Task Sales_import_recosts_an_updated_transaction()
    {
        await using var db = CreateDbContext();
        db.Products.Add(new Product { Id = 10, Name = "Snack", QuantityInStock = 10, AverageUnitCost = 2m });
        db.StockAdjustments.Add(new StockAdjustment
        {
            ProductId = 10,
            QuantityChange = 10,
            QuantityAfter = 10,
            Reason = StockAdjustmentReason.Restock,
            UnitCost = 2m,
            EffectiveAt = new DateTime(2026, 9, 1)
        });
        db.NayaxSales.Add(new NayaxSales
        {
            TransactionID = 1,
            MachineID = 1,
            NayaxProductId = 999,
            ProductName = "Unknown",
            SettlementValue = 5m,
            TransactionStatusId = NayaxTransactionStatusIds.Completed,
            MachineAuthorizationTime = new DateTime(2026, 9, 2)
        });
        await db.SaveChangesAsync();

        await CreateImportService(db).ImportNayaxSalesFromExcelAsync(Csv(
            "TransactionID,TransactionStatusId,MachineID,NayaxProductId,SettlementValue,ProductName,MachineAuthorizationTime\n" +
            "1,12,1,10,5,Snack,2/9/2026 2:30:00 PM"));

        var sale = await db.NayaxSales.SingleAsync();
        Assert.Equal(2m, sale.CostOfGoodsSold);
        Assert.Equal(SaleCostingStatus.Costed, sale.CostingStatus);
    }

    private static AppDbContext CreateDbContext() =>
        TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static ImportService CreateImportService(AppDbContext db, INayaxLynxClient? nayax = null)
    {
        var rebuild = new InventoryCostRebuildService(db);
        return new ImportService(
            db,
            Mock.Of<IWebHostEnvironment>(),
            Mock.Of<ILogger<ImportService>>(),
            nayax ?? Mock.Of<INayaxLynxClient>(),
            new SaleCostingService(db, rebuild),
            rebuild);
    }

    private static IFormFile Csv(string content)
    {
        var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
        return new FormFile(stream, 0, stream.Length, "file", "sales.csv");
    }
}
