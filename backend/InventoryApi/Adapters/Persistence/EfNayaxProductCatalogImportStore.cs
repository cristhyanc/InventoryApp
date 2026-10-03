using Inventory.Application.Imports;
using InventoryApi.Data;
using InventoryApi.Models;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Adapters.Persistence;

/// <summary>
/// Temporary EF Core implementation of <see cref="INayaxProductCatalogImportStore"/> (issue #300).
/// It lives in InventoryApi, not Inventory.Infrastructure, because it depends on
/// <see cref="AppDbContext"/> and the <see cref="Product"/>/<see cref="Category"/> persistence
/// models that still live in InventoryApi, following the same precedent as
/// <c>EfProductStore</c>/<c>EfCategoryStore</c>/<c>EfImportedReimbursementStore</c>. Move it into
/// Inventory.Infrastructure once <see cref="AppDbContext"/> and the shared persistence models
/// relocate there.
///
/// The read-decide-write sequence stays in this adapter, exactly as the former
/// <c>ImportService.ImportProductsAsync</c> ran it, rather than being decomposed into
/// Application-level orchestration - the same ownership precedent <c>EfPurchaseStore</c>'s
/// multi-step writes follow. Both reads are awaited one at a time, because they share the request's
/// scoped context (docs/architecture.md § Concurrency inside one request).
///
/// That shape gives no isolation against a *concurrent* request, and must not be read as if it did.
/// The catalogue reads run outside the transaction <c>SaveChangesAsync</c> opens, and there is no
/// lock, no expected-state comparison and no concurrency token, so two overlapping imports can both
/// decide the same product is new (the later <c>SaveChanges</c> then fails on its primary key), and
/// a local catalogue edit committed between this import's read and its write is simply overwritten
/// by whichever writer commits last. Keeping the sequence in one adapter only keeps the
/// new/existing decision and the write it feeds together within this one request; closing the
/// cross-request window would need an explicit transaction or a concurrency token, which is a
/// behaviour change outside issue #300.
/// </summary>
public sealed class EfNayaxProductCatalogImportStore : INayaxProductCatalogImportStore
{
    private readonly AppDbContext _db;

    public EfNayaxProductCatalogImportStore(AppDbContext db)
    {
        _db = db;
    }

    public async Task ApplyAsync(NayaxProductCatalogImport import, CancellationToken cancellationToken)
    {
        // Tracked reads, scoped to the caller's business by the central AppDbContext query filters
        // alone: the import never carries, and must never apply, a business predicate of its own.
        var localProducts = await _db.Products.ToListAsync(cancellationToken);
        var localCategories = await _db.Categories.ToListAsync(cancellationToken);

        var newCategories = new List<Category>();
        // Only a missing category is created; an existing one keeps its local name.
        foreach (var category in import.Categories.Where(incoming => localCategories.All(local => local.Id != incoming.Id)))
        {
            newCategories.Add(new Category
            {
                Id = category.Id,
                Name = category.Name,
                Description = category.Description,
            });
        }

        var newProducts = new List<Product>();
        foreach (var entry in import.Products)
        {
            var product = localProducts.SingleOrDefault(local => local.Id == entry.Id);
            if (product is null)
            {
                product = new Product
                {
                    Id = entry.Id,
                    RestockTo = 0,
                    CreatedAt = import.ImportedAtUtc,
                };
                newProducts.Add(product);
            }

            // Only the Nayax-managed catalogue fields are written. Stock, costing and the
            // operator's own catalogue edits are local state this import does not own.
            product.Name = entry.Name;
            product.Description = entry.Description;
            product.UnitPrice = entry.UnitPrice;
            product.CategoryId = entry.CategoryId;
            product.UpdatedAt = import.ImportedAtUtc;
        }

        if (newCategories.Count > 0) _db.Categories.AddRange(newCategories);
        if (newProducts.Count > 0) _db.Products.AddRange(newProducts);
        await _db.SaveChangesAsync(cancellationToken);
    }
}
