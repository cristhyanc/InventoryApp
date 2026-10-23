using Inventory.Application.Machines;
using Inventory.Application.MachineStockSync;
using Inventory.Application.Nayax;
using Inventory.Domain.Nayax;
using Inventory.Domain.Providers;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Application.MachineStockSync;

/// <summary>
/// How Sync Restock identifies the product behind an MDB position (issue #733). Machine + MDB still
/// gives the Nayax product id, but the local product is now the one that <em>carries</em> that id -
/// <c>Product.ExternalProductId</c> since issue #759 - read through the real
/// <see cref="EfNayaxProductLinkStore"/>.
///
/// This matters more here than anywhere else in this slice: a matched event is what an operator can
/// then apply as a machine refill, which moves storage inventory. Matching the wrong product would
/// deduct the wrong product's stock.
/// </summary>
public class MachineStockSyncNayaxLinkTests
{
    private const long MachineId = 900;
    private const int Mdb = 13;
    private const long NayaxProductId = 200;
    private const long LocalProductId = 4200;
    private const int SellerBusinessId = 1;
    private const string ProductName = "25g Nobby's Beef Jerky Hot";
    private static readonly DateTime EventTime = new(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// A business that sells through Nayax, acting as itself: since issue #759 a Nayax product id
    /// resolves only through the id recorded on this business's own product row, and only while the
    /// business's <c>SalesProvider</c> is Nayax.
    /// </summary>
    private static AppDbContext CreateDb(string name)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(name).Options;
        using (var setup = TestAppDbContext.Unrestricted(options))
        {
            setup.Businesses.Add(new Business
            {
                Id = SellerBusinessId,
                Name = "Vending A",
                Status = BusinessStatus.Active,
                SalesProvider = ExternalProvider.Nayax,
                CreatedAtUtc = EventTime,
            });
            setup.SaveChanges();
        }

        return TestAppDbContext.For(options, SellerBusinessId);
    }

    private static void SeedProduct(AppDbContext db, long productId, string name, long? nayaxProductId = null) =>
        db.Products.Add(new Product
        {
            Id = productId,
            ExternalProductId = nayaxProductId is { } id ? NayaxExternalId.From(id) : null,
            Name = name,
            QuantityInStock = 20,
            CostingQuantity = 20,
            InventoryValue = 20m,
            AverageUnitCost = 1m,
        });

    private static SyncMachineStockFromNayax UseCase(AppDbContext db)
    {
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachineLastAlertsAsync(MachineId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new NayaxMachineAlert
                {
                    EventLogId = 1,
                    MachineId = MachineId,
                    EventCode = NayaxMachineAlertEventCodes.StockAdjustForMachine,
                    EventDescription = "Stock Adjust for Machine",
                    EventData = $"Eunhye Chung 'Adjusted Stock, Product MDB: {Mdb} | {ProductName} | 2",
                    EventDateTimeGmt = EventTime,
                    EventDateTimeVmc = DateTime.SpecifyKind(EventTime.AddHours(10), DateTimeKind.Unspecified),
                },
            ]);
        nayax.Setup(x => x.GetMachineProductsAsync(MachineId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new NayaxMachineProduct { NayaxProductID = NayaxProductId, MDBCode = Mdb, ProductName = ProductName },
            ]);

        return new SyncMachineStockFromNayax(
            nayax.Object,
            new EfMachineStockEventStore(db, TestCostingUseCases.RecordMovement(db), TestCostingUseCases.Rebuild(db)),
            new EfNayaxProductLinkStore(db));
    }

    [Fact]
    public async Task An_event_matches_the_local_product_that_carries_the_nayax_id()
    {
        using var db = CreateDb(nameof(An_event_matches_the_local_product_that_carries_the_nayax_id));
        SeedProduct(db, LocalProductId, ProductName, NayaxProductId);
        await db.SaveChangesAsync();

        var preview = await UseCase(db).Handle(new MachineIdentity(LocalMachineId: 31, NayaxMachineId: MachineId), CancellationToken.None);

        var evt = Assert.Single(preview.Events);
        Assert.Equal(NayaxStockEventMatchStatus.Matched, evt.MatchStatus);
        Assert.Equal(LocalProductId, evt.MatchedProductId);
        Assert.Equal(ProductName, evt.MatchedProductName);
    }

    /// <summary>
    /// With the Nayax id recorded on no product, the MDB position names no product of this business.
    /// The event stays Needs Review - which is how an unmapped Nayax product was already reported -
    /// and specifically is not matched to the unrelated local product numbered 200, whose stock an
    /// apply would then have deducted.
    /// </summary>
    [Fact]
    public async Task An_unrecorded_nayax_product_is_not_matched_to_the_product_whose_id_equals_it()
    {
        using var db = CreateDb(nameof(An_unrecorded_nayax_product_is_not_matched_to_the_product_whose_id_equals_it));
        SeedProduct(db, NayaxProductId, ProductName);
        await db.SaveChangesAsync();

        var preview = await UseCase(db).Handle(new MachineIdentity(LocalMachineId: 31, NayaxMachineId: MachineId), CancellationToken.None);

        var evt = Assert.Single(preview.Events);
        Assert.Equal(NayaxStockEventMatchStatus.NeedsReview, evt.MatchStatus);
        Assert.Null(evt.MatchedProductId);
        Assert.Contains(
            $"No local product is mapped to Nayax product {ProductName} ({NayaxProductId})", evt.NeedsReviewReason);
    }
}
