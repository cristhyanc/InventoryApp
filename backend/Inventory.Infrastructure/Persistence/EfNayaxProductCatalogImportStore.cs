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
///
/// <para><strong>Identity comes from the link tables, not from the local keys</strong> (issue #745,
/// parent #329 decision A2b). A snapshot entry names a local product or category only through this
/// business's <c>ProductExternalIds</c>/<c>CategoryExternalIds</c> link, and a product's Nayax
/// <c>ProductGroupID</c> is resolved to a local <c>CategoryId</c> the same way - including for a
/// category this same snapshot creates. An unlinked local row is <em>not</em> claimed by a Nayax id
/// that merely equals its key. A row this import creates is still keyed by the Nayax id until #735
/// (A4), so an unlinked local row of this business whose key collides with an incoming Nayax id is
/// refused by name (see <c>RefuseCollidingLocalKey</c>) rather than silently merged into; another
/// business's colliding row is still refused by the shared primary key, as it always was.</para>
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
        // reads' tenant scoping and awaited one at a time for the same reason. The local id beside
        // each ExternalId is what makes this read the identity lookup of the import (issue #745),
        // not only the "has a link already?" check it started as. The text itself stays opaque: the
        // one thing this adapter assumes about it is that NayaxExternalId is what writes it.
        var localProductIdByNayaxId = (await _db.ProductExternalIds
                .Where(link => link.Provider == ExternalProvider.Nayax)
                .Select(link => new { link.ExternalId, link.ProductId })
                .ToListAsync(cancellationToken))
            .ToDictionary(link => link.ExternalId, link => link.ProductId, StringComparer.Ordinal);
        var localCategoryIdByNayaxId = (await _db.CategoryExternalIds
                .Where(link => link.Provider == ExternalProvider.Nayax)
                .Select(link => new { link.ExternalId, link.CategoryId })
                .ToListAsync(cancellationToken))
            .ToDictionary(link => link.ExternalId, link => link.CategoryId, StringComparer.Ordinal);

        var productsByLocalId = localProducts.ToDictionary(product => product.Id);

        // Every row the snapshot named - created or merely updated - ends the import carrying its
        // Nayax id (issue #732). A link that already exists is kept exactly as it is: there is
        // nothing in it to update while Id still *is* the Nayax id, and writing a second one would
        // break the one-link-per-row-per-provider rule. A created row's link joins the lookup
        // immediately, so a product whose group this snapshot just created resolves to it, and a
        // snapshot that repeated an id neither creates a second row nor queues a second link.
        //
        // The local id of a created row is still the remote one, which is what makes its link
        // derivable without matching anything. #735 (A4) is where a new row stops being keyed that
        // way, and this is the write that has to be in place before it can be.
        var newCategories = new List<Category>();
        var newCategoryLinks = new List<CategoryExternalId>();
        foreach (var category in import.Categories)
        {
            var externalId = NayaxExternalId.From(category.Id);

            // Only a group this business has no link for becomes a new category; a linked one keeps
            // its local name. A local category whose Id merely equals the group id is neither used
            // nor updated - it is some other category, as it will plainly be after #735 (A4).
            if (localCategoryIdByNayaxId.ContainsKey(externalId))
                continue;

            RefuseCollidingLocalKey(
                localCategories.Any(local => local.Id == category.Id), "category", category.Id);

            var created = new Category
            {
                Id = category.Id,
                Name = category.Name,
                Description = category.Description,
            };
            newCategories.Add(created);
            localCategoryIdByNayaxId[externalId] = created.Id;
            newCategoryLinks.Add(new CategoryExternalId
            {
                Provider = ExternalProvider.Nayax,
                ExternalId = externalId,
                CategoryId = created.Id,
            });
        }

        var newProducts = new List<Product>();
        var newProductLinks = new List<ProductExternalId>();
        foreach (var entry in import.Products)
        {
            var externalId = NayaxExternalId.From(entry.Id);
            Product product;
            if (localProductIdByNayaxId.TryGetValue(externalId, out var localProductId))
            {
                // The link's (BusinessId, ProductId) foreign key is what guarantees the row it names
                // exists in this business, so a link with no product would be a broken database
                // rather than an unknown id.
                product = productsByLocalId.TryGetValue(localProductId, out var linked)
                    ? linked
                    : throw new InvalidOperationException(
                        $"A Nayax product link names local product {localProductId}, which this business does not hold.");
            }
            else
            {
                RefuseCollidingLocalKey(productsByLocalId.ContainsKey(entry.Id), "product", entry.Id);

                product = new Product
                {
                    Id = entry.Id,
                    RestockTo = 0,
                    CreatedAt = import.ImportedAtUtc,
                };
                newProducts.Add(product);
                productsByLocalId[product.Id] = product;
                localProductIdByNayaxId[externalId] = product.Id;
                newProductLinks.Add(new ProductExternalId
                {
                    Provider = ExternalProvider.Nayax,
                    ExternalId = externalId,
                    ProductId = product.Id,
                });
            }

            // Only the Nayax-managed catalogue fields are written. Stock, costing and the
            // operator's own catalogue edits are local state this import does not own.
            product.Name = entry.Name;
            product.Description = entry.Description;
            product.UnitPrice = entry.UnitPrice;
            product.CategoryId = LocalCategoryId(entry.CategoryId, localCategoryIdByNayaxId);
            product.UpdatedAt = import.ImportedAtUtc;
        }

        if (newCategories.Count > 0) _db.Categories.AddRange(newCategories);
        if (newProducts.Count > 0) _db.Products.AddRange(newProducts);
        if (newCategoryLinks.Count > 0) _db.CategoryExternalIds.AddRange(newCategoryLinks);
        if (newProductLinks.Count > 0) _db.ProductExternalIds.AddRange(newProductLinks);
        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Refuses a snapshot entry whose Nayax id this business has no link for while a local row of
    /// its own already holds that key (issue #745).
    ///
    /// <para>Identity comes from the link table, so that local row is some other product or
    /// category and must not be merged into - but a row this import creates is still keyed by the
    /// Nayax id until #735 (A4), so there is no key left to create it under either. The deliberate,
    /// named failure is the honest answer: nothing is written (the import is one
    /// <c>SaveChanges</c>), and a human links or re-keys the local row and imports again. Issue
    /// #732's backfill linked every row that existed when the link tables arrived, so this needs a
    /// row restored from an older backup or created locally under a colliding key.</para>
    ///
    /// <para>It carries only the ids - no names, prices or operator data - because it reaches logs
    /// and the generic error response.</para>
    /// </summary>
    private static void RefuseCollidingLocalKey(bool collides, string rowKind, long nayaxId)
    {
        if (!collides)
            return;

        throw new InvalidOperationException(
            $"Nayax {rowKind} {nayaxId} has no provider link in this business, and a local {rowKind} " +
            $"already holds the id {nayaxId}. The catalogue import will not merge them: link the " +
            $"local {rowKind} to its Nayax id, or give it a different id, and import again.");
    }

    /// <summary>
    /// The local category a product's Nayax <c>ProductGroupID</c> names (issue #745). The Lynx
    /// contract declares that field as <c>integer&lt;int32&gt;</c>, nullable, "the unique identifier
    /// of the product group to which the product belongs" (<c>GET
    /// /v1/operators/{OperatorID}/products</c>), and the group endpoint issues ids from that same
    /// space (<c>GET /v1/operators/{ActorID}/productGroups</c>), which is what makes one link table
    /// the answer for both.
    ///
    /// <para>A group this business holds no link for leaves the product uncategorised rather than
    /// pointing it at whichever local category happens to carry that number - the group is unknown,
    /// and <c>Category.Id</c> stopped being the place a Nayax id is read from. A product with no
    /// group at all is unchanged: it has nothing to resolve.</para>
    /// </summary>
    private static long? LocalCategoryId(
        long? nayaxProductGroupId, IReadOnlyDictionary<string, long> localCategoryIdByNayaxId) =>
        nayaxProductGroupId is long groupId
        && localCategoryIdByNayaxId.TryGetValue(NayaxExternalId.From(groupId), out var localCategoryId)
            ? localCategoryId
            : null;
}
