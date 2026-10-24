using System.Linq.Expressions;
using System.Text;

using Inventory.Application.Products;
using Inventory.Domain.Products;
using Inventory.Domain.Reporting.Dashboard;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using DomainStock = Inventory.Domain.Stock;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IProductCatalogStore"/>. It lives in
/// Inventory.Infrastructure, following the same precedent as
/// <see cref="EfCategoryStore"/>/<see cref="EfProductStore"/>: <see cref="AppDbContext"/> and
/// <see cref="Product"/> moved there in issue #307, and this adapter family followed them in issue
/// #309 (Persistence 8/8 of #153).
///
/// Tenant scoping is the central <see cref="AppDbContext"/> global query filter, never a predicate
/// here (AGENTS.md § Tenant ownership and data isolation). The category/supplier/stock-adjustment
/// detail is loaded with explicit <c>Include</c>s rather than lazily (issue #52), so a caller still
/// sees it after this context is disposed.
///
/// <para>Since issue #737 the two reads a person maintains the catalogue from - the listing and the
/// single product - also answer whether each product may be deleted, from the same
/// <see cref="ProductDeletionPolicy"/> the delete is refused by; see
/// <see cref="WithDeletionEligibilityAsync"/> for why that is advisory and what it costs.</para>
/// </summary>
public sealed class EfProductCatalogStore : IProductCatalogStore
{
    private readonly AppDbContext _db;

    public EfProductCatalogStore(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<ProductRecord>> ListOrderedByNameAsync(
        ProductCatalogFilter filter, CancellationToken cancellationToken) =>
        await WithDeletionEligibilityAsync(
            await Filtered(_db.Products, filter)
                .OrderBy(product => product.Name)
                .ToListAsync(cancellationToken),
            cancellationToken);

    public async Task<IReadOnlyList<ProductRecord>> ListUnorderedAsync(
        ProductCatalogFilter filter, CancellationToken cancellationToken) =>
        await WithInventoryHistoryAsync(
            await Filtered(_db.Products.AsNoTracking(), filter).ToListAsync(cancellationToken),
            cancellationToken);

    public async Task<ProductRecord?> FindAsync(long id, CancellationToken cancellationToken)
    {
        var product = await WithDetail(_db.Products)
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (product is null) return null;

        var records = await WithDeletionEligibilityAsync([product], cancellationToken);
        return records[0];
    }

    /// <summary>
    /// The character <see cref="LikePattern"/> writes before a <c>%</c>, <c>_</c> or itself, so a
    /// character <c>LIKE</c> treats specially (issue #827) is matched literally when a person types
    /// it into a search rather than acting as a wildcard.
    /// </summary>
    private const char SearchEscapeCharacter = '\\';

    private static IQueryable<Product> Filtered(IQueryable<Product> products, ProductCatalogFilter filter)
    {
        var query = WithDetail(products);

        // Retirement, in SQL rather than after the read (issue #736): a retired product is not in
        // the catalogue a person works from, and a listing that leaves it out should not pay to read
        // it and its include graph first. Only the filter decides this - the adapter states no rule
        // of its own, and FindAsync below is deliberately not filtered, so a retired product's
        // history and its reactivation stay reachable.
        if (!filter.IncludeRetired) query = query.Where(product => product.IsActive);

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            // LIKE rather than Contains (issue #827): EF Core's SQLite provider translates Contains
            // to instr(...), which SQLite compares byte-for-byte, so a search typed in a different
            // case than the stored name or SKU found nothing. SQLite's LIKE, unlike instr, folds
            // ASCII letter case by default - the only case-insensitivity SQLite offers without the
            // collation change this issue excludes - though it still leaves non-ASCII letters
            // unfolded. The escape character keeps a literal '%' or '_' a person types from acting
            // as a wildcard.
            var pattern = $"%{LikePattern(filter.Search)}%";
            var escapeCharacter = SearchEscapeCharacter.ToString();
            query = query.Where(product => EF.Functions.Like(product.Name, pattern, escapeCharacter)
                || (product.Sku != null && EF.Functions.Like(product.Sku, pattern, escapeCharacter)));
        }

        if (filter.CategoryId.HasValue) query = query.Where(product => product.CategoryId == filter.CategoryId);

        return query;
    }

    /// <summary>
    /// <paramref name="value"/> with every character <c>LIKE</c> treats specially - <c>%</c>,
    /// <c>_</c> and <see cref="SearchEscapeCharacter"/> itself - preceded by the escape character, so
    /// each is matched literally once the result is wrapped in the leading and trailing <c>%</c> that
    /// make the search a substring match.
    /// </summary>
    private static string LikePattern(string value)
    {
        var pattern = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (character is '%' or '_' or SearchEscapeCharacter) pattern.Append(SearchEscapeCharacter);
            pattern.Append(character);
        }

        return pattern.ToString();
    }

    private static IQueryable<Product> WithDetail(IQueryable<Product> products) =>
        products
            .Include(product => product.Category)
            .Include(product => product.StockAdjustments);

    /// <summary>
    /// The same records, each carrying whether anything records inventory ever having been in it
    /// (issue #861): the evidence the home Dashboard's valuation needs to tell a product that has
    /// never held inventory from one whose inventory is genuinely unknown.
    ///
    /// <para><strong>Only the products that could still be empty are asked about.</strong> A product
    /// whose own stored columns already establish a position - an inventory value, a costing quantity
    /// or storage stock - is valued or unknown on those columns alone, so its history would change
    /// nothing; <see cref="ProductInventoryValuationFact.CouldNeverHaveHeldInventory"/> is the Domain
    /// rule that decides which, read here rather than restated. Every product being stocked is the
    /// normal case, and then this costs no query at all.</para>
    ///
    /// <para>Resolved <see langword="false"/> rather than left <see langword="null"/> for the rest:
    /// "this product holds a costing position" is itself the answer that it is not an empty one, and a
    /// caller must not have to tell "no history" from "not asked" for a row that was in fact decided.</para>
    /// </summary>
    private async Task<IReadOnlyList<ProductRecord>> WithInventoryHistoryAsync(
        IReadOnlyList<Product> products, CancellationToken cancellationToken)
    {
        if (products.Count == 0) return [];

        var candidates = products
            .Where(product => ProductInventoryValuationFact.CouldNeverHaveHeldInventory(
                product.InventoryValue, product.CostingQuantity, product.QuantityInStock))
            .Select(product => product.Id)
            .ToHashSet();
        var withHistory = await EfProductInventoryHistoryQueries.WithRecordedInventoryHistoryAsync(
            _db, candidates, cancellationToken);

        return products
            .Select(product => ToRecord(product) with
            {
                HasInventoryHistory = !candidates.Contains(product.Id) || withHistory.Contains(product.Id),
            })
            .ToList();
    }

    /// <summary>
    /// The same records, each carrying the deletion rule's own answer for it (issue #737): whether
    /// <c>DELETE api/products/{id}</c> would allow the delete, and the refusal it would give
    /// otherwise. The screens delete from these two reads, and a <strong>Delete</strong> that must
    /// fail should not be offered at all.
    ///
    /// <para><strong>The rule is not restated here.</strong> The counts are handed to
    /// <see cref="ProductDeletionEligibility.Decide"/>, which calls the one
    /// <see cref="ProductDeletionPolicy"/> the delete itself is refused by, so what a client offers
    /// and what the server permits cannot drift apart - the same reason
    /// <c>ProviderManagedCatalogueFields</c> produces both the published lock list and the update's
    /// refusal.</para>
    ///
    /// <para><strong>It is advisory, and the delete still decides.</strong> These counts are taken
    /// outside the delete's transaction, so a product can gain a stock movement or a provider link
    /// straight after this read; <see cref="EfProductStore.DeleteAsync"/> counts again inside its own
    /// transaction and refuses there. That transactional recheck is what makes the delete safe - this
    /// read only spares an operator an action that is already known to be impossible.</para>
    ///
    /// <para><strong>Cost.</strong> Six grouped counts for the whole page, not six per product: each
    /// reads one table once for every listed product id, so a catalogue of any size costs the same
    /// fixed number of round trips. The seventh kind, the stock movements, is counted from the rows
    /// <see cref="WithDetail"/> already loaded onto each product for the record's own history rather
    /// than read a second time.</para>
    /// </summary>
    private async Task<IReadOnlyList<ProductRecord>> WithDeletionEligibilityAsync(
        IReadOnlyList<Product> products, CancellationToken cancellationToken)
    {
        if (products.Count == 0) return [];

        var references = await CountReferencesAsync(products, cancellationToken);

        // Read only to name the provider in its refusal, and only when a listed product carries one
        // of its ids - a catalogue nobody but this business maintains asks nothing extra.
        var provider = products.Any(product => product.ExternalProductId is not null)
            ? await EfSalesProviderQueries.CurrentBusinessSalesProviderAsync(_db, cancellationToken)
            : null;

        return products
            .Select(product => ToRecord(product) with
            {
                DeletionEligibility = ProductDeletionEligibility.Decide(
                    product.ExternalProductId is not null, provider, references[product.Id]),
            })
            .ToList();
    }

    /// <summary>
    /// What refers to each of these products, counted the way
    /// <see cref="EfProductStore"/> counts it for one - the same six kinds, from the same tables, so
    /// the advisory answer and the delete's own answer are about the same rows. Sales are counted
    /// through the sale's own persisted <c>ProductId</c> (issue #758), never a provider id or a name.
    /// </summary>
    private async Task<IReadOnlyDictionary<long, ProductDeletionReferences>> CountReferencesAsync(
        IReadOnlyList<Product> products, CancellationToken cancellationToken)
    {
        var ids = products.Select(product => product.Id).ToList();

        var purchaseLines = await CountByProductAsync(
            _db.ReceiptItems.Where(item => ids.Contains(item.ProductId)),
            item => item.ProductId,
            cancellationToken);
        var supplierOrderLines = await CountByProductAsync(
            _db.SupplierOrderLines.Where(line => ids.Contains(line.ProductId)),
            line => line.ProductId,
            cancellationToken);
        var machineCounts = await CountByProductAsync(
            _db.MachineStockCounts.Where(count => ids.Contains(count.ProductId)),
            count => count.ProductId,
            cancellationToken);
        var baselines = await CountByProductAsync(
            _db.InventoryCostTransitionBaselines.Where(baseline => ids.Contains(baseline.ProductId)),
            baseline => baseline.ProductId,
            cancellationToken);
        var repairs = await CountByProductAsync(
            _db.InventoryCostRepairs.Where(repair => ids.Contains(repair.ProductId)),
            repair => repair.ProductId,
            cancellationToken);

        // The sale's product column is nullable - an unresolved sale belongs to no product at all
        // (issue #758) - so the null is excluded before the grouping rather than counted as a product.
        var sales = (await _db.NayaxSales
                .Where(sale => sale.ProductId != null && ids.Contains(sale.ProductId.Value))
                .GroupBy(sale => sale.ProductId)
                .Select(group => new { ProductId = group.Key, Count = group.Count() })
                .ToListAsync(cancellationToken))
            .ToDictionary(row => row.ProductId!.Value, row => row.Count);

        return products.ToDictionary(
            product => product.Id,
            product => new ProductDeletionReferences(
                // Already loaded by WithDetail for the record's own history; counting them in SQL
                // again would read rows this adapter is holding.
                StockAdjustments: product.StockAdjustments.Count,
                PurchaseLines: CountFor(purchaseLines, product.Id),
                SupplierOrderLines: CountFor(supplierOrderLines, product.Id),
                Sales: CountFor(sales, product.Id),
                MachineCounts: CountFor(machineCounts, product.Id),
                CostingRecords: CountFor(baselines, product.Id) + CountFor(repairs, product.Id)));
    }

    /// <summary>
    /// How many rows of one table each listed product has, in one grouped read. Rows of other
    /// businesses are excluded by the central query filters, exactly as they are for the delete's own
    /// counts.
    /// </summary>
    private static Task<Dictionary<long, int>> CountByProductAsync<TRow>(
        IQueryable<TRow> rows, Expression<Func<TRow, long>> productId, CancellationToken cancellationToken) =>
        rows.GroupBy(productId)
            .Select(group => new { ProductId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.ProductId, row => row.Count, cancellationToken);

    /// <summary>A product no row of that table refers to is absent from the grouping, not zero in it.</summary>
    private static int CountFor(IReadOnlyDictionary<long, int> counts, long productId) =>
        counts.TryGetValue(productId, out var count) ? count : 0;

    private static ProductRecord ToRecord(Product product) => new()
    {
        Id = product.Id,
        Name = product.Name,
        Sku = product.Sku,
        Description = product.Description,
        AverageUnitCost = product.AverageUnitCost,
        CostingQuantity = product.CostingQuantity,
        InventoryValue = product.InventoryValue,
        QuantityInStock = product.QuantityInStock,
        LowStockThreshold = product.LowStockThreshold,
        RestockTo = product.RestockTo,
        Unit = product.Unit,
        IsActive = product.IsActive,
        // Whether the business's sales provider names this product, never the id itself (issue
        // #736): a provider's identifier is integration state, and what a reader of the catalogue
        // needs is which fields are locked.
        IsProviderManaged = product.ExternalProductId is not null,
        CreatedAt = product.CreatedAt,
        UpdatedAt = product.UpdatedAt,
        CategoryId = product.CategoryId,
        Category = product.Category is null
            ? null
            : new ProductCategoryRecord(product.Category.Id, product.Category.Name, product.Category.Description),
        StockAdjustments = product.StockAdjustments.Select(adjustment => new ProductStockAdjustmentRecord(
            adjustment.Id,
            adjustment.ProductId,
            adjustment.ReceiptItemId,
            adjustment.QuantityChange,
            adjustment.QuantityAfter,
            adjustment.UnitCost,
            adjustment.TotalCost,
            adjustment.CostingQuantityAfter,
            adjustment.AverageUnitCostAfter,
            adjustment.InventoryValueAfter,
            (DomainStock.StockAdjustmentReason)adjustment.Reason,
            (DomainStock.StockAdjustmentSource)adjustment.Source,
            adjustment.MachineId,
            adjustment.Notes,
            adjustment.EatBefore,
            adjustment.CreatedAt,
            adjustment.EffectiveAt)).ToList(),
    };
}
