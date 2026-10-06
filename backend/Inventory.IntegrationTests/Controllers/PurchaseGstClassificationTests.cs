using Inventory.Application.Documents;
using Inventory.Application.Purchases;
using Inventory.Domain.Gst;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using InventoryApi.Controllers;
using InventoryApi.DTOs;
using InventoryApi.Tests.Application.Time;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// The purchase GST classification round trip (issue #429), driven through the real
/// <see cref="PurchasesController"/> over the real <see cref="EfPurchaseStore"/> on relational
/// SQLite: what a caller submits on create and edit, what is persisted with it, and what comes back
/// on the wire.
///
/// Each request uses its own <see cref="AppDbContext"/>, as separate HTTP requests would, so no
/// shared change tracker can make an unpersisted classification look stored.
/// </summary>
public sealed class PurchaseGstClassificationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public PurchaseGstClassificationTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var setup = TestAppDbContext.Unrestricted(_options);
        setup.Database.EnsureCreated();
        setup.Products.AddRange(
            new Product { Id = 1, Name = "Coke" },
            new Product { Id = 2, Name = "Chips" });
        // Both products start from an authoritative costing baseline before PurchaseDate, so the
        // purchase's own cost rebuild has a defined starting point to replay from.
        setup.InventoryCostTransitionBaselines.AddRange(Baseline(1), Baseline(2));
        setup.SaveChanges();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Create_stores_and_returns_the_submitted_classifications_as_manual()
    {
        var created = await Upload(
            deliveryCost: 5m,
            deliveryGst: GstClassification.Taxable,
            packageCost: 2m,
            packageGst: GstClassification.GstFree,
            items: [new PurchaseItemDto(1, 10m, 1.10m, GstClassification.Taxable)]);

        AssertCharges(created, (GstClassification.Taxable, GstClassificationSource.Manual),
            (GstClassification.GstFree, GstClassificationSource.Manual));
        var item = Assert.Single(created.Items);
        Assert.Equal(GstClassification.Taxable, item.GstClassification);
        Assert.Equal(GstClassificationSource.Manual, item.GstClassificationSource);

        await using var verify = TestAppDbContext.Unrestricted(_options);
        var stored = await verify.Receipts.Include(r => r.Items).SingleAsync();
        Assert.Equal(GstClassification.Taxable, stored.DeliveryGstClassification);
        Assert.Equal(GstClassificationSource.Manual, stored.DeliveryGstClassificationSource);
        Assert.Equal(GstClassification.GstFree, stored.PackageGstClassification);
        Assert.Equal(GstClassificationSource.Manual, stored.PackageGstClassificationSource);
        Assert.Equal(GstClassification.Taxable, stored.Items.Single().GstClassification);
        Assert.Equal(GstClassificationSource.Manual, stored.Items.Single().GstClassificationSource);
    }

    /// <summary>
    /// Decision D4: nothing is inferred. A purchase submitted without classifications - the shape
    /// every existing client sends - stays entirely unclassified.
    /// </summary>
    [Fact]
    public async Task Create_without_classifications_leaves_every_component_unknown()
    {
        var created = await Upload(
            deliveryCost: 5m, deliveryGst: null, packageCost: 2m, packageGst: null,
            items: [new PurchaseItemDto(1, 10m, 1.10m)]);

        AssertCharges(created, (GstClassification.Unknown, GstClassificationSource.Unknown),
            (GstClassification.Unknown, GstClassificationSource.Unknown));
        var item = Assert.Single(created.Items);
        Assert.Equal(GstClassification.Unknown, item.GstClassification);
        Assert.Equal(GstClassificationSource.Unknown, item.GstClassificationSource);
    }

    /// <summary>
    /// Decision D3: a charge that is absent or zero has no classification, even when the caller
    /// submits one for it.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(0d)]
    public async Task An_absent_or_zero_charge_is_never_classified(double? chargeAmount)
    {
        var amount = chargeAmount is null ? (decimal?)null : (decimal)chargeAmount.Value;

        var created = await Upload(
            deliveryCost: amount,
            deliveryGst: GstClassification.Taxable,
            packageCost: amount,
            packageGst: GstClassification.Taxable,
            items: []);

        AssertCharges(created, (GstClassification.Unknown, GstClassificationSource.Unknown),
            (GstClassification.Unknown, GstClassificationSource.Unknown));
    }

    [Fact]
    public async Task Update_keeps_the_classifications_the_caller_did_not_resubmit()
    {
        var created = await Upload(
            deliveryCost: 5m,
            deliveryGst: GstClassification.Taxable,
            packageCost: 2m,
            packageGst: GstClassification.GstFree,
            items: [new PurchaseItemDto(1, 10m, 1.10m, GstClassification.Taxable)]);

        // An ordinary edit: new title, same charges, same line, no classification submitted at all.
        var updated = await Update(
            created.Id, deliveryCost: 5m, deliveryGst: null, packageCost: 2m, packageGst: null,
            items: [new PurchaseItemDto(1, 10m, 1.10m)]);

        AssertCharges(updated, (GstClassification.Taxable, GstClassificationSource.Manual),
            (GstClassification.GstFree, GstClassificationSource.Manual));
        Assert.Equal(GstClassification.Taxable, Assert.Single(updated.Items).GstClassification);
    }

    [Fact]
    public async Task Update_replaces_a_resubmitted_classification()
    {
        var created = await Upload(
            deliveryCost: 5m,
            deliveryGst: GstClassification.Taxable,
            packageCost: 2m,
            packageGst: GstClassification.Taxable,
            items: [new PurchaseItemDto(1, 10m, 1.10m, GstClassification.Taxable)]);

        var updated = await Update(
            created.Id, deliveryCost: 5m, deliveryGst: GstClassification.GstFree, packageCost: 2m,
            packageGst: GstClassification.Taxable,
            items: [new PurchaseItemDto(1, 10m, 1.10m, GstClassification.GstFree)]);

        AssertCharges(updated, (GstClassification.GstFree, GstClassificationSource.Manual),
            (GstClassification.Taxable, GstClassificationSource.Manual));
        Assert.Equal(GstClassification.GstFree, Assert.Single(updated.Items).GstClassification);
    }

    /// <summary>
    /// Removing the charge removes its classification with it (decision D3): there is nothing left
    /// to classify, so it must not be left behind as a stale taxable state.
    /// </summary>
    [Fact]
    public async Task Update_clearing_a_charge_clears_its_classification()
    {
        var created = await Upload(
            deliveryCost: 5m,
            deliveryGst: GstClassification.Taxable,
            packageCost: 2m,
            packageGst: GstClassification.Taxable,
            items: []);

        var updated = await Update(created.Id, deliveryCost: null, deliveryGst: null, packageCost: 0m, packageGst: null, items: []);

        AssertCharges(updated, (GstClassification.Unknown, GstClassificationSource.Unknown),
            (GstClassification.Unknown, GstClassificationSource.Unknown));
    }

    /// <summary>
    /// GST classification is accounting data only. Reclassifying a line must not move a cent of
    /// inventory cost: the AVCO state, the inventory value and the restock stock movement the
    /// purchase created all stay exactly as they were (AGENTS.md § Inventory and historical costing
    /// invariants; parent issue #62, "Inventory costing remains separate").
    /// </summary>
    [Fact]
    public async Task Changing_a_classification_does_not_change_inventory_cost()
    {
        var created = await Upload(
            deliveryCost: 5m, deliveryGst: null, packageCost: null, packageGst: null,
            items: [new PurchaseItemDto(1, 10m, 1.10m), new PurchaseItemDto(2, 4m, 2.25m)]);

        var before = await CostStateAsync();

        var updated = await Update(
            created.Id, deliveryCost: 5m, deliveryGst: GstClassification.Taxable, packageCost: null, packageGst: null,
            items:
            [
                new PurchaseItemDto(1, 10m, 1.10m, GstClassification.Taxable),
                new PurchaseItemDto(2, 4m, 2.25m, GstClassification.GstFree),
            ]);

        Assert.Equal(
            [GstClassification.Taxable, GstClassification.GstFree],
            updated.Items.OrderBy(item => item.ProductId).Select(item => item.GstClassification));
        Assert.Equal(before, await CostStateAsync());
    }

    /// <summary>
    /// The product AVCO state and the restock movements a purchase produced, as one comparable
    /// value. Quantities, unit costs, costing quantity, inventory value and the movements' own
    /// costed-after values are all included, because a classification must not shift any of them.
    /// </summary>
    private async Task<string> CostStateAsync()
    {
        await using var db = TestAppDbContext.Unrestricted(_options);
        var products = await db.Products.AsNoTracking().OrderBy(p => p.Id)
            .Select(p => $"{p.Id}:{p.QuantityInStock}:{p.CostingQuantity}:{p.InventoryValue}:{p.AverageUnitCost}")
            .ToListAsync();
        var movements = await db.StockAdjustments.AsNoTracking().OrderBy(m => m.Id)
            .Select(m => $"{m.ProductId}:{m.QuantityChange}:{m.UnitCost}:{m.TotalCost}:" +
                $"{m.CostingQuantityAfter}:{m.InventoryValueAfter}:{m.AverageUnitCostAfter}:{m.EffectiveAt:O}")
            .ToListAsync();
        return string.Join("|", products.Concat(movements));
    }

    private static void AssertCharges(
        PurchaseResponse purchase,
        (GstClassification Classification, GstClassificationSource Source) delivery,
        (GstClassification Classification, GstClassificationSource Source) package)
    {
        Assert.Equal(delivery, (purchase.DeliveryGstClassification, purchase.DeliveryGstClassificationSource));
        Assert.Equal(package, (purchase.PackageGstClassification, purchase.PackageGstClassificationSource));
    }

    private async Task<PurchaseResponse> Upload(
        decimal? deliveryCost,
        GstClassification? deliveryGst,
        decimal? packageCost,
        GstClassification? packageGst,
        IReadOnlyList<PurchaseItemDto> items)
    {
        await using var db = TestAppDbContext.Unrestricted(_options);
        var result = await CreateController(db).Upload(
            CreateFile(), "Weekly restock", null, null, deliveryCost, deliveryGst, packageCost, packageGst,
            new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc), null, Serialize(items));

        var created = result.Result as CreatedAtActionResult;
        Assert.True(created is not null, $"Upload was rejected: {(result.Result as ObjectResult)?.Value}");
        return Assert.IsType<PurchaseResponseDto>(created!.Value).Purchase;
    }

    private async Task<PurchaseResponse> Update(
        int id,
        decimal? deliveryCost,
        GstClassification? deliveryGst,
        decimal? packageCost,
        GstClassification? packageGst,
        IReadOnlyList<PurchaseItemDto> items)
    {
        await using var db = TestAppDbContext.Unrestricted(_options);
        var result = await CreateController(db).Update(
            id, "Weekly restock", null, null, deliveryCost, deliveryGst, packageCost, packageGst,
            new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc), null, Serialize(items));

        return Assert.IsType<PurchaseResponseDto>(Assert.IsType<OkObjectResult>(result.Result).Value).Purchase;
    }

    private static string Serialize(IReadOnlyList<PurchaseItemDto> items) =>
        System.Text.Json.JsonSerializer.Serialize(items, new System.Text.Json.JsonSerializerOptions(
            System.Text.Json.JsonSerializerDefaults.Web));

    private static PurchasesController CreateController(AppDbContext db)
    {
        var store = new EfPurchaseStore(db, TestCostingUseCases.Rebuild(db));
        var documents = new Mock<IDocumentStorage>();
        documents
            .Setup(d => d.SaveAsync(It.IsAny<DocumentCategory>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return new PurchasesController(
            new ListPurchases(store),
            new GetPurchase(store),
            new GetPurchaseFile(store, documents.Object),
            new UploadPurchase(store, documents.Object, new FakeClock(new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc))),
            new UpdatePurchase(store),
            new DeletePurchase(store, documents.Object),
            new ComputePurchaseTotalValidation());
    }

    private static InventoryCostTransitionBaseline Baseline(long productId) => new()
    {
        ProductId = productId,
        CutoffAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        HomeStockQuantity = 0,
        CostSource = InventoryCostBaselineSource.ManualAuthoritative,
    };

    private static IFormFile CreateFile()
    {
        var content = new MemoryStream([1, 2, 3]);
        var file = new Mock<IFormFile>();
        file.Setup(f => f.Length).Returns(content.Length);
        file.Setup(f => f.FileName).Returns("scan.jpg");
        file.Setup(f => f.ContentType).Returns("image/jpeg");
        file.Setup(f => f.OpenReadStream()).Returns(content);
        return file.Object;
    }
}
