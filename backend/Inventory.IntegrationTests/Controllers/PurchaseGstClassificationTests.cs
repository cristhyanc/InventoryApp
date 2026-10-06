using Inventory.Application.Documents;
using Inventory.Application.Purchases;
using Inventory.Domain.Gst;
using Inventory.Domain.Purchases;
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

    /// <summary>
    /// Shared across every request one test makes, so a rejected create can be shown to have
    /// stored no document either - rejecting the request after the scan was written would still be
    /// a state change.
    /// </summary>
    private readonly Mock<IDocumentStorage> _documents = new();

    public PurchaseGstClassificationTests()
    {
        _documents
            .Setup(d => d.SaveAsync(It.IsAny<DocumentCategory>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
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

    #region Undefined classifications are rejected

    /// <summary>
    /// An integer outside the published enum is not a classification. Before issue #429's repair it
    /// was persisted with provenance <c>Manual</c> and then calculated as zero GST without ever
    /// being counted as unresolved, so the purchase endpoints refuse it instead - and refuse it
    /// before a document is stored or a row is written.
    /// </summary>
    [Theory]
    [InlineData(999)]
    [InlineData(-1)]
    [InlineData(3)]
    public async Task Create_rejects_an_undefined_line_classification_and_stores_nothing(int value)
    {
        var result = await UploadResult(
            deliveryCost: 5m, deliveryGst: null, packageCost: null, packageGst: null,
            items: [new PurchaseItemDto(1, 10m, 1.10m, (GstClassification)value)]);

        Assert.Equal(PurchaseGstPolicy.UnsupportedClassificationMessage, RejectionMessage(result));
        await AssertNothingWasStoredAsync();
    }

    [Theory]
    [InlineData(999)]
    [InlineData(-1)]
    public async Task Create_rejects_an_undefined_charge_classification_and_stores_nothing(int value)
    {
        var delivery = await UploadResult(
            deliveryCost: 5m, deliveryGst: (GstClassification)value, packageCost: null, packageGst: null,
            items: []);
        var package = await UploadResult(
            deliveryCost: null, deliveryGst: null, packageCost: 2m, packageGst: (GstClassification)value,
            items: []);

        Assert.Equal(PurchaseGstPolicy.UnsupportedClassificationMessage, RejectionMessage(delivery));
        Assert.Equal(PurchaseGstPolicy.UnsupportedClassificationMessage, RejectionMessage(package));
        await AssertNothingWasStoredAsync();
    }

    /// <summary>
    /// The edit path refuses the same values, and leaves the purchase, its classifications and the
    /// whole costing position exactly as they were.
    /// </summary>
    [Theory]
    [InlineData(999)]
    [InlineData(-1)]
    public async Task Update_rejects_an_undefined_classification_and_changes_nothing(int value)
    {
        var created = await Upload(
            deliveryCost: 5m,
            deliveryGst: GstClassification.Taxable,
            packageCost: 2m,
            packageGst: GstClassification.GstFree,
            items: [new PurchaseItemDto(1, 10m, 1.10m, GstClassification.Taxable)]);
        var purchasesBefore = await PurchaseStateAsync();
        var costBefore = await CostStateAsync();

        var line = await UpdateResult(
            created.Id, deliveryCost: 5m, deliveryGst: null, packageCost: 2m, packageGst: null,
            items: [new PurchaseItemDto(1, 20m, 2.20m, (GstClassification)value)]);
        var charge = await UpdateResult(
            created.Id, deliveryCost: 9m, deliveryGst: (GstClassification)value, packageCost: 2m, packageGst: null,
            items: [new PurchaseItemDto(1, 10m, 1.10m)]);

        Assert.Equal(PurchaseGstPolicy.UnsupportedClassificationMessage, RejectionMessage(line));
        Assert.Equal(PurchaseGstPolicy.UnsupportedClassificationMessage, RejectionMessage(charge));
        Assert.Equal(purchasesBefore, await PurchaseStateAsync());
        Assert.Equal(costBefore, await CostStateAsync());
    }

    /// <summary>
    /// The rejection must not have narrowed what a valid request means: an omitted field is still
    /// "not submitted", and an explicit <c>Unknown</c> is still a supported, storable state.
    /// </summary>
    [Fact]
    public async Task An_explicit_unknown_classification_is_still_accepted()
    {
        var created = await Upload(
            deliveryCost: 5m,
            deliveryGst: GstClassification.Unknown,
            packageCost: 2m,
            packageGst: GstClassification.Unknown,
            items: [new PurchaseItemDto(1, 10m, 1.10m, GstClassification.Unknown)]);

        AssertCharges(created, (GstClassification.Unknown, GstClassificationSource.Unknown),
            (GstClassification.Unknown, GstClassificationSource.Unknown));
        Assert.Equal(GstClassification.Unknown, Assert.Single(created.Items).GstClassification);
    }

    #endregion

    #region Duplicate-product line identity

    /// <summary>
    /// Two lines for one product with different classifications, reordered on the way back with
    /// their ids and no GST field submitted at all: each line keeps its own classification and
    /// provenance, rather than the queue handing the first line's state to whichever line came
    /// first in the request.
    /// </summary>
    [Fact]
    public async Task Update_reordering_identified_duplicate_product_lines_keeps_each_classification()
    {
        var created = await UploadDuplicateProductLines();
        var (taxable, gstFree) = DuplicateLines(created);

        var updated = await Update(
            created.Id, deliveryCost: null, deliveryGst: null, packageCost: null, packageGst: null,
            items:
            [
                new PurchaseItemDto(1, gstFree.Quantity, gstFree.UnitCost, Id: gstFree.Id),
                new PurchaseItemDto(1, taxable.Quantity, taxable.UnitCost, Id: taxable.Id),
            ]);

        AssertLineStates(
            updated,
            (taxable.Id, 2m, 1.00m, GstClassification.Taxable),
            (gstFree.Id, 3m, 2.00m, GstClassification.GstFree));
    }

    /// <summary>
    /// Removing either one of the two lines by id: the line that stays keeps its own classification
    /// and the other's is gone with it, whichever of the two was removed.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Update_removing_one_identified_duplicate_product_line_keeps_the_survivors_classification(
        bool removeTheTaxableLine)
    {
        var created = await UploadDuplicateProductLines();
        var (taxable, gstFree) = DuplicateLines(created);
        var survivor = removeTheTaxableLine ? gstFree : taxable;

        var updated = await Update(
            created.Id, deliveryCost: null, deliveryGst: null, packageCost: null, packageGst: null,
            items: [new PurchaseItemDto(1, survivor.Quantity, survivor.UnitCost, Id: survivor.Id)]);

        var remaining = Assert.Single(updated.Items);
        Assert.Equal(survivor.Id, remaining.Id);
        Assert.Equal(
            removeTheTaxableLine ? GstClassification.GstFree : GstClassification.Taxable,
            remaining.GstClassification);
        Assert.Equal(GstClassificationSource.Manual, remaining.GstClassificationSource);
    }

    /// <summary>
    /// An ordinary quantity/cost edit that submits no GST field at all: both lines keep the
    /// classification and provenance they already had, on the right line.
    /// </summary>
    [Fact]
    public async Task Update_editing_identified_duplicate_product_lines_keeps_each_classification()
    {
        var created = await UploadDuplicateProductLines();
        var (taxable, gstFree) = DuplicateLines(created);

        var updated = await Update(
            created.Id, deliveryCost: null, deliveryGst: null, packageCost: null, packageGst: null,
            items:
            [
                new PurchaseItemDto(1, 7m, 1.50m, Id: taxable.Id),
                new PurchaseItemDto(1, 4m, 2.25m, Id: gstFree.Id),
            ]);

        AssertLineStates(
            updated,
            (taxable.Id, 7m, 1.50m, GstClassification.Taxable),
            (gstFree.Id, 4m, 2.25m, GstClassification.GstFree));
    }

    /// <summary>
    /// With no ids there is no safe answer, so the edit is refused and the stored state is
    /// untouched - rather than silently reclassifying a line.
    /// </summary>
    [Fact]
    public async Task Update_refuses_to_guess_between_disagreeing_duplicate_product_lines()
    {
        var created = await UploadDuplicateProductLines();
        var before = await PurchaseStateAsync();
        var costBefore = await CostStateAsync();

        var reordered = await UpdateResult(
            created.Id, deliveryCost: null, deliveryGst: null, packageCost: null, packageGst: null,
            items: [new PurchaseItemDto(1, 3m, 2.00m), new PurchaseItemDto(1, 2m, 1.00m)]);
        var removed = await UpdateResult(
            created.Id, deliveryCost: null, deliveryGst: null, packageCost: null, packageGst: null,
            items: [new PurchaseItemDto(1, 2m, 1.00m)]);

        Assert.Equal(PurchaseLineIdentityPolicy.AmbiguousLineMessage, RejectionMessage(reordered));
        Assert.Equal(PurchaseLineIdentityPolicy.AmbiguousLineMessage, RejectionMessage(removed));
        Assert.Equal(before, await PurchaseStateAsync());
        Assert.Equal(costBefore, await CostStateAsync());
    }

    /// <summary>
    /// Backward compatibility for every client that predates line ids: duplicate-product lines that
    /// agree about their classification - which is every purchase that existed before issue #429,
    /// all <c>Unknown</c>/<c>Unknown</c> - still match by product, in order, exactly as before.
    /// </summary>
    [Fact]
    public async Task Update_without_ids_still_matches_duplicate_product_lines_that_agree()
    {
        var created = await Upload(
            deliveryCost: null, deliveryGst: null, packageCost: null, packageGst: null,
            items: [new PurchaseItemDto(1, 2m, 1.00m), new PurchaseItemDto(1, 3m, 2.00m)]);
        var ids = created.Items.OrderBy(item => item.Id).Select(item => item.Id).ToList();

        var updated = await Update(
            created.Id, deliveryCost: null, deliveryGst: null, packageCost: null, packageGst: null,
            items: [new PurchaseItemDto(1, 5m, 1.25m), new PurchaseItemDto(1, 6m, 2.50m)]);

        Assert.Equal(ids, updated.Items.OrderBy(item => item.Id).Select(item => item.Id));
        Assert.All(updated.Items, item => Assert.Equal(GstClassification.Unknown, item.GstClassification));
        Assert.Equal(
            [(5m, 1.25m), (6m, 2.50m)],
            updated.Items.OrderBy(item => item.Id).Select(item => (item.Quantity, item.UnitCost)));
    }

    /// <summary>
    /// A submitted id must name a line of this purchase: an id repeated, an id that does not exist
    /// and an id belonging to a different purchase are all refused, and nothing is written.
    /// </summary>
    [Fact]
    public async Task Update_rejects_a_duplicate_unknown_or_other_purchases_line_id()
    {
        var created = await UploadDuplicateProductLines();
        var (taxable, _) = DuplicateLines(created);
        var otherPurchase = await Upload(
            deliveryCost: null, deliveryGst: null, packageCost: null, packageGst: null,
            items: [new PurchaseItemDto(2, 4m, 2.25m, GstClassification.Taxable)]);
        var otherPurchasesLineId = Assert.Single(otherPurchase.Items).Id;
        var before = await PurchaseStateAsync();

        var duplicate = await UpdateResult(
            created.Id, deliveryCost: null, deliveryGst: null, packageCost: null, packageGst: null,
            items:
            [
                new PurchaseItemDto(1, 2m, 1.00m, Id: taxable.Id),
                new PurchaseItemDto(1, 3m, 2.00m, Id: taxable.Id),
            ]);
        var unknown = await UpdateResult(
            created.Id, deliveryCost: null, deliveryGst: null, packageCost: null, packageGst: null,
            items: [new PurchaseItemDto(1, 2m, 1.00m, Id: 987654)]);
        var foreign = await UpdateResult(
            created.Id, deliveryCost: null, deliveryGst: null, packageCost: null, packageGst: null,
            items: [new PurchaseItemDto(1, 2m, 1.00m, Id: otherPurchasesLineId)]);

        Assert.Equal(PurchaseLineIdentityPolicy.DuplicateLineIdMessage, RejectionMessage(duplicate));
        Assert.Equal(PurchaseLineIdentityPolicy.UnknownLineIdMessage, RejectionMessage(unknown));
        Assert.Equal(PurchaseLineIdentityPolicy.UnknownLineIdMessage, RejectionMessage(foreign));
        Assert.Equal(before, await PurchaseStateAsync());
    }

    [Fact]
    public async Task Update_rejects_moving_an_identified_line_to_another_product()
    {
        var created = await UploadDuplicateProductLines();
        var (taxable, gstFree) = DuplicateLines(created);
        var before = await PurchaseStateAsync();

        var result = await UpdateResult(
            created.Id, deliveryCost: null, deliveryGst: null, packageCost: null, packageGst: null,
            items:
            [
                new PurchaseItemDto(2, 2m, 1.00m, Id: taxable.Id),
                new PurchaseItemDto(1, 3m, 2.00m, Id: gstFree.Id),
            ]);

        Assert.Equal(PurchaseLineIdentityPolicy.ProductChangedMessage, RejectionMessage(result));
        Assert.Equal(before, await PurchaseStateAsync());
    }

    /// <summary>
    /// A classification-only edit of duplicate-product lines moves no cent of inventory cost: the
    /// AVCO state and both restock movements are identical afterwards (AGENTS.md § Purchase GST
    /// classification - classification is accounting data only).
    /// </summary>
    [Fact]
    public async Task Reclassifying_identified_duplicate_product_lines_does_not_change_inventory_cost()
    {
        var created = await UploadDuplicateProductLines();
        var (taxable, gstFree) = DuplicateLines(created);
        var before = await CostStateAsync();

        var updated = await Update(
            created.Id, deliveryCost: null, deliveryGst: null, packageCost: null, packageGst: null,
            items:
            [
                new PurchaseItemDto(1, 2m, 1.00m, GstClassification.GstFree, taxable.Id),
                new PurchaseItemDto(1, 3m, 2.00m, GstClassification.Taxable, gstFree.Id),
            ]);

        AssertLineStates(
            updated,
            (taxable.Id, 2m, 1.00m, GstClassification.GstFree),
            (gstFree.Id, 3m, 2.00m, GstClassification.Taxable));
        Assert.Equal(before, await CostStateAsync());
    }

    #endregion

    /// <summary>
    /// One purchase holding two lines for the same product with deliberately different
    /// classifications and different amounts - the shape the product-id queue could not tell apart.
    /// </summary>
    private Task<PurchaseResponse> UploadDuplicateProductLines() => Upload(
        deliveryCost: null,
        deliveryGst: null,
        packageCost: null,
        packageGst: null,
        items:
        [
            new PurchaseItemDto(1, 2m, 1.00m, GstClassification.Taxable),
            new PurchaseItemDto(1, 3m, 2.00m, GstClassification.GstFree),
        ]);

    /// <summary>The taxable and the GST-free line of <see cref="UploadDuplicateProductLines"/>.</summary>
    private static (PurchaseItemResponse Taxable, PurchaseItemResponse GstFree) DuplicateLines(
        PurchaseResponse purchase)
    {
        Assert.Equal(2, purchase.Items.Count);
        return (
            purchase.Items.Single(item => item.GstClassification == GstClassification.Taxable),
            purchase.Items.Single(item => item.GstClassification == GstClassification.GstFree));
    }

    private static void AssertLineStates(
        PurchaseResponse purchase,
        params (int Id, decimal Quantity, decimal UnitCost, GstClassification Classification)[] expected)
    {
        Assert.Equal(
            expected.OrderBy(line => line.Id).ToArray(),
            purchase.Items.OrderBy(item => item.Id)
                .Select(item => (item.Id, item.Quantity, item.UnitCost, item.GstClassification))
                .ToArray());
        Assert.All(purchase.Items, item => Assert.Equal(GstClassificationSource.Manual, item.GstClassificationSource));
    }

    private async Task AssertNothingWasStoredAsync()
    {
        await using var db = TestAppDbContext.Unrestricted(_options);
        Assert.Empty(await db.Receipts.AsNoTracking().ToListAsync());
        Assert.Empty(await db.ReceiptItems.AsNoTracking().ToListAsync());
        Assert.Empty(await db.StockAdjustments.AsNoTracking().ToListAsync());
        _documents.Verify(
            d => d.SaveAsync(It.IsAny<DocumentCategory>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()),
            Times.Never);
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
        var result = await UploadResult(deliveryCost, deliveryGst, packageCost, packageGst, items);

        var created = result.Result as CreatedAtActionResult;
        Assert.True(created is not null, $"Upload was rejected: {(result.Result as ObjectResult)?.Value}");
        return Assert.IsType<PurchaseResponseDto>(created!.Value).Purchase;
    }

    private async Task<ActionResult<PurchaseResponseDto>> UploadResult(
        decimal? deliveryCost,
        GstClassification? deliveryGst,
        decimal? packageCost,
        GstClassification? packageGst,
        IReadOnlyList<PurchaseItemDto> items)
    {
        await using var db = TestAppDbContext.Unrestricted(_options);
        return await CreateController(db).Upload(
            CreateFile(), "Weekly restock", null, null, deliveryCost, deliveryGst, packageCost, packageGst,
            new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc), null, Serialize(items));
    }

    private async Task<PurchaseResponse> Update(
        int id,
        decimal? deliveryCost,
        GstClassification? deliveryGst,
        decimal? packageCost,
        GstClassification? packageGst,
        IReadOnlyList<PurchaseItemDto> items)
    {
        var result = await UpdateResult(id, deliveryCost, deliveryGst, packageCost, packageGst, items);

        return Assert.IsType<PurchaseResponseDto>(Assert.IsType<OkObjectResult>(result.Result).Value).Purchase;
    }

    private async Task<ActionResult<PurchaseResponseDto>> UpdateResult(
        int id,
        decimal? deliveryCost,
        GstClassification? deliveryGst,
        decimal? packageCost,
        GstClassification? packageGst,
        IReadOnlyList<PurchaseItemDto> items)
    {
        await using var db = TestAppDbContext.Unrestricted(_options);
        return await CreateController(db).Update(
            id, "Weekly restock", null, null, deliveryCost, deliveryGst, packageCost, packageGst,
            new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc), null, Serialize(items));
    }

    /// <summary>
    /// The complete persisted purchase state the GST tests compare before and after a rejected
    /// request: every purchase's charges and classifications, and every line's product, quantity,
    /// unit cost, classification and provenance.
    /// </summary>
    private async Task<string> PurchaseStateAsync()
    {
        await using var db = TestAppDbContext.Unrestricted(_options);
        var purchases = await db.Receipts.AsNoTracking().OrderBy(r => r.Id)
            .Select(r => $"{r.Id}:{r.Title}:{r.DeliveryCost}:{r.DeliveryGstClassification}:" +
                $"{r.DeliveryGstClassificationSource}:{r.PackageCost}:{r.PackageGstClassification}:" +
                $"{r.PackageGstClassificationSource}")
            .ToListAsync();
        var items = await db.ReceiptItems.AsNoTracking().OrderBy(i => i.Id)
            .Select(i => $"{i.Id}:{i.ReceiptId}:{i.ProductId}:{i.Quantity}:{i.UnitCost}:" +
                $"{i.GstClassification}:{i.GstClassificationSource}")
            .ToListAsync();
        return string.Join("|", purchases.Concat(items));
    }

    private static string RejectionMessage(ActionResult<PurchaseResponseDto> result) =>
        Assert.IsType<string>(Assert.IsType<BadRequestObjectResult>(result.Result).Value);

    private static string Serialize(IReadOnlyList<PurchaseItemDto> items) =>
        System.Text.Json.JsonSerializer.Serialize(items, new System.Text.Json.JsonSerializerOptions(
            System.Text.Json.JsonSerializerDefaults.Web));

    private PurchasesController CreateController(AppDbContext db)
    {
        var store = new EfPurchaseStore(db, TestCostingUseCases.Rebuild(db));
        var documents = _documents.Object;
        return new PurchasesController(
            new ListPurchases(store),
            new GetPurchase(store),
            new GetPurchaseFile(store, documents),
            new UploadPurchase(store, documents, new FakeClock(new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc))),
            new UpdatePurchase(store),
            new DeletePurchase(store, documents),
            new ComputePurchaseTotalValidation(),
            new ComputePurchaseGstSummary());
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
