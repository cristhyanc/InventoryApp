using Inventory.Application.Imports;
using Inventory.Domain.Providers;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="INayaxProductCatalogImportStore"/> (issue #300).
/// It lives in Inventory.Infrastructure, following the same precedent as
/// <see cref="EfProductStore"/>/<see cref="EfCategoryStore"/>/<see cref="EfImportedReimbursementStore"/>:
/// <see cref="AppDbContext"/> and the <see cref="Product"/>/<see cref="Category"/> persistence
/// models it depends on moved there in issue #307, and this adapter family followed them in issue
/// #309 (Persistence 8/8 of #153).
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

        // The Nayax ids this business has already recorded (issue #732), read with the same two
        // reads' tenant scoping and awaited one at a time for the same reason. Only the text is
        // read: an ExternalId is opaque, and the one thing this adapter assumes about it is that
        // NayaxExternalId is what writes it.
        var linkedProducts = (await _db.ProductExternalIds
                .Where(link => link.Provider == ExternalProvider.Nayax)
                .Select(link => link.ExternalId)
                .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);
        var linkedCategories = (await _db.CategoryExternalIds
                .Where(link => link.Provider == ExternalProvider.Nayax)
                .Select(link => link.ExternalId)
                .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);

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

        // Every row the snapshot named - created or merely updated - ends the import carrying its
        // Nayax id (issue #732). A link that already exists is kept exactly as it is: there is
        // nothing in it to update while Id still *is* the Nayax id, and writing a second one would
        // break the one-link-per-product-per-provider rule. The queued ids join the set so a
        // snapshot that repeated an id does not queue the same link twice either.
        //
        // The local id is still the remote one here, which is what makes the link derivable without
        // matching anything. #735 (A4) is where a new row stops being keyed that way, and this is
        // the write that has to be in place before it can be.
        var newCategoryLinks = new List<CategoryExternalId>();
        foreach (var category in import.Categories)
        {
            var externalId = NayaxExternalId.From(category.Id);
            if (linkedCategories.Add(externalId))
            {
                newCategoryLinks.Add(new CategoryExternalId
                {
                    Provider = ExternalProvider.Nayax,
                    ExternalId = externalId,
                    CategoryId = category.Id,
                });
            }
        }

        var newProductLinks = new List<ProductExternalId>();
        foreach (var entry in import.Products)
        {
            var externalId = NayaxExternalId.From(entry.Id);
            if (linkedProducts.Add(externalId))
            {
                newProductLinks.Add(new ProductExternalId
                {
                    Provider = ExternalProvider.Nayax,
                    ExternalId = externalId,
                    ProductId = entry.Id,
                });
            }
        }

        if (newCategories.Count > 0) _db.Categories.AddRange(newCategories);
        if (newProducts.Count > 0) _db.Products.AddRange(newProducts);
        if (newCategoryLinks.Count > 0) _db.CategoryExternalIds.AddRange(newCategoryLinks);
        if (newProductLinks.Count > 0) _db.ProductExternalIds.AddRange(newProductLinks);
        await _db.SaveChangesAsync(cancellationToken);
    }
}
