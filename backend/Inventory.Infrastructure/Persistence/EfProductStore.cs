using Inventory.Application.Costing;
using Inventory.Application.Products;
using Inventory.Domain.Gst;
using Inventory.Domain.Products;
using Inventory.Domain.Providers;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IProductStore"/>. It lives in Inventory.Infrastructure,
/// following the same precedent as <see cref="EfCategoryStore"/>/
/// <see cref="EfSupplierStore"/>/<see cref="EfOperatingExpenseStore"/>: <see cref="AppDbContext"/>
/// and the persistence models it depends on moved there in issue #307, and this adapter family
/// followed them in issue #309 (Persistence 8/8 of #153).
///
/// <para><strong>Four refusals are decided here, each beside the write it guards</strong>
/// (issue #736, and the unit label of issue #895). A field the business's sales provider manages, a
/// category the business does not own, a product history already refers to, and an update that would
/// blank a stored unit label are all statements about <em>stored</em> rows, so
/// each is read, compared and acted on as one operation - inside a relational transaction, and with
/// the rule itself taken from <see cref="ProviderManagedCatalogueFields"/>/
/// <see cref="ProductDeletionPolicy"/>/<see cref="ProductCataloguePolicy"/> rather than written
/// again here. A use-case-level read
/// followed by a separate write would be a window in which a product can gain a stock movement that
/// the delete then cascades away.</para>
///
/// Tenant scoping stays the central <see cref="AppDbContext"/> query filters and
/// <c>BusinessOwnershipEnforcer</c>: every read below is filtered to the caller's business already,
/// which is also what makes "a category this business does not own" indistinguishable from "no such
/// category" - deliberately (AGENTS.md § Tenant ownership and data isolation).
/// </summary>
public sealed class EfProductStore : IProductStore
{
    private readonly AppDbContext _db;
    private readonly IRebuildProductCost _rebuild;

    public EfProductStore(AppDbContext db, IRebuildProductCost rebuild)
    {
        _db = db;
        _rebuild = rebuild;
    }

    /// <summary>
    /// Creates the product, its opening stock movement and the costing position that movement
    /// implies as <strong>one</strong> relational transaction (issue #736).
    ///
    /// <para>The three writes used to be three separate commits, which was tolerable while this use
    /// case was unreachable and is not now that <c>POST /api/products</c> is a public stock-writing
    /// path: a cancellation or a failure after the first commit left a product carrying
    /// <c>QuantityInStock</c> with no <c>StockAdjustment</c> behind it, or a movement with no cost
    /// rebuilt from it, under an HTTP request the caller saw fail and would retry - which would then
    /// create a second product. Either way the ledger no longer explains the quantity, which is
    /// exactly what AGENTS.md § Inventory and historical costing invariants forbids. Now nothing at
    /// all is committed unless all three succeed; disposing the transaction without the commit rolls
    /// the product back with them.</para>
    /// </summary>
    public async Task<ProductWriteAttempt> CreateAsync(
        ProductCreateFields fields, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fields);

        if (NameOrRefusal(fields.Name) is not { } name)
            return ProductWriteAttempt.Refused(ProductCataloguePolicy.NameRequiredMessage);

        // A new product has a unit label (issue #895). Checked before the transaction opens, like
        // the name above and for the same reason: the create use case has already applied this rule,
        // and applying it again at the write keeps the column from being written blank by a future
        // caller that forgets. Nothing is written when it refuses.
        if (ProductCataloguePolicy.NormalizeUnit(fields.Unit) is not { } unit)
            return ProductWriteAttempt.Refused(ProductCataloguePolicy.UnitRequiredMessage);

        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(cancellationToken)
            : null;

        if (!await CategoryIsOwnedAsync(fields.CategoryId, cancellationToken))
            return ProductWriteAttempt.Refused(ProductCataloguePolicy.UnknownCategoryMessage);

        var product = new Product
        {
            Name = name,
            Sku = fields.Sku,
            Description = fields.Description,
            AverageUnitCost = fields.InitialUnitCost ?? 0m,
            IsActive = fields.IsActive,
            QuantityInStock = fields.QuantityInStock,
            LowStockThreshold = fields.LowStockThreshold,
            RestockTo = fields.RestockTo,
            Unit = unit,
            CategoryId = fields.CategoryId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Products.Add(product);
        await _db.SaveChangesAsync(cancellationToken);

        if (product.QuantityInStock > 0)
        {
            _db.StockAdjustments.Add(new StockAdjustment
            {
                ProductId = product.Id,
                QuantityChange = product.QuantityInStock,
                QuantityAfter = product.QuantityInStock,
                Reason = StockAdjustmentReason.Restock,
                UnitCost = fields.InitialUnitCost,
                TotalCost = fields.InitialUnitCost * product.QuantityInStock,
                Notes = "Initial stock on product creation",
                EffectiveAt = product.CreatedAt,
            });
            await _db.SaveChangesAsync(cancellationToken);

            if (fields.InitialUnitCost.HasValue)
            {
                await _rebuild.RebuildAsync(product.Id, cancellationToken: cancellationToken);
                await _db.SaveChangesAsync(cancellationToken);
            }
        }

        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return ProductWriteAttempt.Succeeded(product.Id);
    }

    public Task<bool> ExistsAsync(long id, CancellationToken cancellationToken) =>
        _db.Products.AnyAsync(product => product.Id == id, cancellationToken);

    /// <summary>
    /// Writes the editable fields, refusing a change the business's sales provider owns, a category
    /// the business does not, and a request that would blank a stored unit label. Submitting the
    /// stored value is not a change, which is what lets a client send the whole product back.
    ///
    /// <para>The unit-label refusal is the fourth decided here (issue #895), because it is the same
    /// shape as the other three: it compares the <em>stored</em> label, so the read, the comparison
    /// and the write are one operation inside this transaction, and a product that gains a label
    /// between a separate read and this write cannot have it blanked. The rule itself is
    /// <see cref="ProductCataloguePolicy.UnitChangeRefusal"/> and is not restated here.</para>
    ///
    /// <para>Since issue #938 the same transaction also refreshes the product's price-tracking
    /// mapping, if it has one: a mapping still awaiting a match follows the product's name and unit
    /// label, and a change of the unit label returns a matched mapping to awaiting a match with the
    /// new text. It is decided here for the same reason - the comparison is against stored text - and
    /// it refuses nothing, so an edit of an untracked product is byte for byte what it always was. See
    /// <see cref="TrackedProductSearchTextRefresh"/>.</para>
    /// </summary>
    public async Task<ProductWriteAttempt> UpdateAsync(
        long id, ProductUpdateFields fields, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fields);

        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(cancellationToken)
            : null;

        var product = await _db.Products.FirstOrDefaultAsync(
            candidate => candidate.Id == id, cancellationToken);
        if (product is null) return ProductWriteAttempt.NotFound();

        if (NameOrRefusal(fields.Name) is not { } name)
            return ProductWriteAttempt.Refused(ProductCataloguePolicy.NameRequiredMessage);

        if (ProductCataloguePolicy.UnitChangeRefusal(product.Unit, fields.Unit) is { } unitRefusal)
            return ProductWriteAttempt.Refused(unitRefusal);

        if (product.ExternalProductId is not null)
        {
            var provider = await CurrentSalesProviderAsync(cancellationToken);
            if (!string.Equals(product.Name, name, StringComparison.Ordinal))
                return ProductWriteAttempt.Refused(
                    ProviderManagedCatalogueFields.ProductNameRefusal(provider));
            if (product.CategoryId != fields.CategoryId)
                return ProductWriteAttempt.Refused(
                    ProviderManagedCatalogueFields.ProductCategoryRefusal(provider));
        }

        if (product.CategoryId != fields.CategoryId
            && !await CategoryIsOwnedAsync(fields.CategoryId, cancellationToken))
            return ProductWriteAttempt.Refused(ProductCataloguePolicy.UnknownCategoryMessage);

        var updatedAt = DateTime.UtcNow;

        product.Name = name;
        product.Sku = fields.Sku;
        product.Description = fields.Description;
        product.IsActive = fields.IsActive;
        product.LowStockThreshold = fields.LowStockThreshold;
        product.RestockTo = fields.RestockTo;
        // A nonblank submitted label is stored trimmed; a blank one is only reachable for a product
        // whose stored label is already blank (the refusal above), and that row keeps the blank it
        // has rather than being rewritten or backfilled (issue #895).
        product.Unit = ProductCataloguePolicy.NormalizeUnit(fields.Unit) ?? product.Unit;
        product.CategoryId = fields.CategoryId;
        product.UpdatedAt = updatedAt;

        // The fifth thing decided here, and the one that writes another table (issue #938): the
        // price-tracking mapping this product may have follows its name and unit label. It is in
        // this transaction for the same reason the four refusals above are - the rule compares the
        // mapping's stored text with the text being written, so the read, the comparison and the
        // write are one operation. It creates and removes nothing, and leaves an untracked product
        // untouched; the rule is TrackedProductSearchTextRefresh's and is not restated here.
        await TrackedProductSearchTextRefresh.ApplyAsync(
            _db, product.Id, product.Name, product.Unit, updatedAt, cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return ProductWriteAttempt.Succeeded(product.Id);
    }

    /// <summary>
    /// Deletes the product only while <see cref="ProductDeletionPolicy"/> allows it. The counts and
    /// the delete share one transaction, because two of the references cascade: a product that
    /// gained a stock movement or a machine stock count between a separate count and this delete
    /// would have that history erased rather than the delete refused.
    /// </summary>
    public async Task<ProductWriteAttempt> DeleteAsync(long id, CancellationToken cancellationToken)
    {
        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(cancellationToken)
            : null;

        var product = await _db.Products.FirstOrDefaultAsync(
            candidate => candidate.Id == id, cancellationToken);
        if (product is null) return ProductWriteAttempt.NotFound();

        var references = await CountReferencesAsync(id, cancellationToken);
        var provider = product.ExternalProductId is null
            ? null
            : await CurrentSalesProviderAsync(cancellationToken);

        if (ProductDeletionPolicy.Refusal(product.ExternalProductId is not null, provider, references)
            is { } refusal)
            return ProductWriteAttempt.Refused(refusal);

        _db.Products.Remove(product);
        await _db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return ProductWriteAttempt.Succeeded(id);
    }

    public async Task<GstClassification?> FindGstRuleAsync(long id, CancellationToken cancellationToken)
    {
        var product = await _db.Products
            .AsNoTracking()
            .Where(product => product.Id == id)
            .Select(product => new { product.GstRule })
            .SingleOrDefaultAsync(cancellationToken);

        return product?.GstRule;
    }

    /// <summary>
    /// Writes the rule column only. <c>UpdatedAt</c> is deliberately left alone as well, because it
    /// describes the catalogue record the Nayax import and the product edit maintain; a GST rule is
    /// separate bookkeeping configuration and must not look like a catalogue change.
    /// </summary>
    public async Task<bool> SetGstRuleAsync(long id, GstClassification rule, CancellationToken cancellationToken)
    {
        var product = await _db.Products.FindAsync([id], cancellationToken);
        if (product is null) return false;

        product.GstRule = rule;
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// The name as it is stored - trimmed, because trailing space is not a different product - or
    /// <see langword="null"/> when there is none.
    ///
    /// <para>The use cases validate the name before calling this adapter
    /// (<see cref="ProductCataloguePolicy"/>); this is the same rule applied again at the write
    /// itself, so the column cannot be written blank by a future caller that forgets, and it reads
    /// the one message rather than stating the rule a second time.</para>
    /// </summary>
    private static string? NameOrRefusal(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : name.Trim();

    /// <summary>
    /// Whether the product may carry <paramref name="categoryId"/>: no category at all is always
    /// allowed, and a named one has to be visible through the tenant query filters, which is what
    /// keeps a product's category inside its own business.
    /// </summary>
    private async Task<bool> CategoryIsOwnedAsync(long? categoryId, CancellationToken cancellationToken) =>
        categoryId is not long id
            || await _db.Categories.AnyAsync(category => category.Id == id, cancellationToken);

    /// <summary>
    /// Everything that already refers to the product, counted in one place so
    /// <see cref="ProductDeletionPolicy"/> can say what is in the way. Sales are counted through the
    /// sale's own persisted <c>ProductId</c> (issue #758) and never by a provider id or a name.
    /// </summary>
    private async Task<ProductDeletionReferences> CountReferencesAsync(
        long id, CancellationToken cancellationToken) => new(
            StockAdjustments: await _db.StockAdjustments
                .CountAsync(adjustment => adjustment.ProductId == id, cancellationToken),
            PurchaseLines: await _db.ReceiptItems
                .CountAsync(item => item.ProductId == id, cancellationToken),
            SupplierOrderLines: await _db.SupplierOrderLines
                .CountAsync(line => line.ProductId == id, cancellationToken),
            Sales: await _db.NayaxSales
                .CountAsync(sale => sale.ProductId == id, cancellationToken),
            MachineCounts: await _db.MachineStockCounts
                .CountAsync(count => count.ProductId == id, cancellationToken),
            CostingRecords: await _db.InventoryCostTransitionBaselines
                    .CountAsync(baseline => baseline.ProductId == id, cancellationToken)
                + await _db.InventoryCostRepairs
                    .CountAsync(repair => repair.ProductId == id, cancellationToken));

    /// <summary>
    /// The caller's business's sales provider, read only to name it in a refusal. A row carrying an
    /// external id always belongs to a business whose provider is set (issue #759), so this is the
    /// provider that issued that id.
    /// </summary>
    private Task<ExternalProvider?> CurrentSalesProviderAsync(CancellationToken cancellationToken) =>
        EfSalesProviderQueries.CurrentBusinessSalesProviderAsync(_db, cancellationToken);
}
