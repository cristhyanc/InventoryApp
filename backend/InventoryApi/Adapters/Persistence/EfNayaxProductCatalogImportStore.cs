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
/// multi-step writes follow. Keeping the catalogue read and the upsert in one operation on one
/// scoped <c>DbContext</c> is also what keeps the new/existing decision and the write from being
/// separated by a window another request could change the catalogue in. Both reads are awaited one
/// at a time, because they share that scoped context (docs/architecture.md § Concurrency inside one
/// request).
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
        foreach (var category in import.Categories)
        {
            // Only a missing category is created; an existing one keeps its local name.
            if (localCategories.All(local => local.Id != category.Id))
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
