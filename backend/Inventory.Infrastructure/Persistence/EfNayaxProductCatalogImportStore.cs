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
/// <para><strong>Identity comes from the external id columns, not from the local keys</strong>
/// (issue #745, parent #329 decision A2b; issue #759 moved it from the link tables onto the rows). A
/// snapshot entry names a local product or category only through that row's
/// <c>ExternalProductId</c>/<c>ExternalCategoryId</c>, and a product's Nayax <c>ProductGroupID</c> is
/// resolved to a local <c>CategoryId</c> the same way - including for a category this same snapshot
/// creates. A local row with no external id is <em>not</em> claimed by a Nayax id that merely equals
/// its key. A row this import creates is still keyed by the Nayax id until #735 (A4), so a local row
/// of this business with no external id whose key collides with an incoming Nayax id is refused by
/// name (see <c>RefuseCollidingLocalKey</c>) rather than silently merged into; another business's
/// colliding row is still refused by the shared primary key, as it always was.</para>
///
/// <para><strong>It writes the business's own provider's ids, and refuses to do anything else</strong>
/// (issue #759). The external id columns hold the id issued by <c>Business.SalesProvider</c>, so this
/// import reads and writes them as Nayax ids only while that provider is Nayax - which storing Nayax
/// credentials is what establishes, and a catalogue import cannot run without them. A business whose
/// provider is anything else is a state this import refuses by name, before it writes anything,
/// rather than recording Nayax ids in a column another system's ids are read from.</para>
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
        // Asked before anything is read or written, because the answer decides whether this
        // business's external id columns hold Nayax ids at all (issue #759).
        await RequireNayaxSalesProviderAsync(cancellationToken);

        // Tracked reads, scoped to the caller's business by the central AppDbContext query filters
        // alone: the import never carries, and must never apply, a business predicate of its own.
        var localProducts = await _db.Products.ToListAsync(cancellationToken);
        var localCategories = await _db.Categories.ToListAsync(cancellationToken);

        // The Nayax ids this business has already recorded, taken from the two reads above rather
        // than from a third query: since issue #759 the id is a column on the local row, so the rows
        // this import already has in hand are the identity lookup (issue #745). The local id beside
        // each external id is what makes it one. The text itself stays opaque: the one thing this
        // adapter assumes about it is that NayaxExternalId is what writes it.
        var localProductIdByNayaxId = localProducts
            .Where(product => product.ExternalProductId is not null)
            .ToDictionary(product => product.ExternalProductId!, product => product.Id, StringComparer.Ordinal);
        var localCategoryIdByNayaxId = localCategories
            .Where(category => category.ExternalCategoryId is not null)
            .ToDictionary(category => category.ExternalCategoryId!, category => category.Id, StringComparer.Ordinal);

        var productsByLocalId = localProducts.ToDictionary(product => product.Id);

        // Every row the snapshot named - created or merely updated - ends the import carrying its
        // Nayax id (issue #732, as a column since #759). A row that already carries one is left
        // exactly as it is: it is the same id, and rewriting it would be the one way this import
        // could move an identifier between rows. A created row's id joins the lookup immediately, so
        // a product whose group this snapshot just created resolves to it, and a snapshot that
        // repeated an id neither creates a second row nor assigns the id twice.
        //
        // The local id of a created row is still the remote one, which is what makes its external id
        // derivable without matching anything. #735 (A4) is where a new row stops being keyed that
        // way, and this is the write that has to be in place before it can be.
        var newCategories = new List<Category>();
        foreach (var category in import.Categories)
        {
            var externalId = NayaxExternalId.From(category.Id);

            // Only a group this business has recorded no category for becomes a new category; a
            // recorded one keeps its local name. A local category whose Id merely equals the group id
            // is neither used nor updated - it is some other category, as it will plainly be after
            // #735 (A4).
            if (localCategoryIdByNayaxId.ContainsKey(externalId))
                continue;

            RefuseCollidingLocalKey(
                localCategories.Any(local => local.Id == category.Id), "category", category.Id);

            var created = new Category
            {
                Id = category.Id,
                ExternalCategoryId = externalId,
                Name = category.Name,
                Description = category.Description,
            };
            newCategories.Add(created);
            localCategoryIdByNayaxId[externalId] = created.Id;
        }

        var newProducts = new List<Product>();
        foreach (var entry in import.Products)
        {
            var externalId = NayaxExternalId.From(entry.Id);
            Product product;
            if (localProductIdByNayaxId.TryGetValue(externalId, out var localProductId))
            {
                // The lookup was built from the product rows this import already read, so a local id
                // it does not hold would be a broken dictionary rather than an unknown id.
                product = productsByLocalId.TryGetValue(localProductId, out var recorded)
                    ? recorded
                    : throw new InvalidOperationException(
                        $"A Nayax product id names local product {localProductId}, which this business does not hold.");
            }
            else
            {
                RefuseCollidingLocalKey(productsByLocalId.ContainsKey(entry.Id), "product", entry.Id);

                product = new Product
                {
                    Id = entry.Id,
                    ExternalProductId = externalId,
                    RestockTo = 0,
                    CreatedAt = import.ImportedAtUtc,
                };
                newProducts.Add(product);
                productsByLocalId[product.Id] = product;
                localProductIdByNayaxId[externalId] = product.Id;
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
        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Refuses to run at all unless the current business's sales provider is Nayax (issue #759).
    ///
    /// <para>The external id columns this import reads and writes hold the id issued by
    /// <c>Business.SalesProvider</c>, and nothing says which system that was. Storing the Nayax
    /// credentials this import authenticates with is what sets that provider, so in every reachable
    /// state the answer is already Nayax; a business where it is not is a broken invariant rather
    /// than a user error, and writing Nayax ids into a column read as another provider's would be
    /// exactly the silent identity error this slice exists to end. The refusal is raised before the
    /// catalogue is read and the import is one <c>SaveChanges</c>, so nothing is written.</para>
    /// </summary>
    private async Task RequireNayaxSalesProviderAsync(CancellationToken cancellationToken)
    {
        if (await EfSalesProviderQueries.CurrentBusinessSellsThroughAsync(
                _db, ExternalProvider.Nayax, cancellationToken))
            return;

        throw new InvalidOperationException(
            "The Nayax product catalogue cannot be imported into a business whose sales provider is "
                + "not Nayax. Store the business's Nayax credentials first, which is what records "
                + "its sales provider. Nothing was imported.");
    }

    /// <summary>
    /// Refuses a snapshot entry whose Nayax id this business has recorded nowhere while a local row
    /// of its own already holds that key (issue #745).
    ///
    /// <para>Identity comes from the external id column, so that local row is some other product or
    /// category and must not be merged into - but a row this import creates is still keyed by the
    /// Nayax id until #735 (A4), so there is no key left to create it under either. The deliberate,
    /// named failure is the honest answer: nothing is written (the import is one
    /// <c>SaveChanges</c>), and a human records the id on the local row or re-keys it and imports
    /// again. Issue #732's backfill, and the #759 copy that followed it, recorded an id for every row
    /// that existed, so this needs a row restored from an older backup or created locally under a
    /// colliding key.</para>
    ///
    /// <para>It carries only the ids - no names, prices or operator data - because it reaches logs
    /// and the generic error response.</para>
    /// </summary>
    private static void RefuseCollidingLocalKey(bool collides, string rowKind, long nayaxId)
    {
        if (!collides)
            return;

        throw new InvalidOperationException(
            $"Nayax {rowKind} {nayaxId} is recorded on no {rowKind} in this business, and a local " +
            $"{rowKind} already holds the id {nayaxId}. The catalogue import will not merge them: " +
            $"record the Nayax id on the local {rowKind}, or give it a different id, and import again.");
    }

    /// <summary>
    /// The local category a product's Nayax <c>ProductGroupID</c> names (issue #745). The Lynx
    /// contract declares that field as <c>integer&lt;int32&gt;</c>, nullable, "the unique identifier
    /// of the product group to which the product belongs" (<c>GET
    /// /v1/operators/{OperatorID}/products</c>), and the group endpoint issues ids from that same
    /// space (<c>GET /v1/operators/{ActorID}/productGroups</c>), which is what makes one column -
    /// <c>Category.ExternalCategoryId</c> - the answer for both.
    ///
    /// <para>A group this business has recorded on no category leaves the product uncategorised
    /// rather than pointing it at whichever local category happens to carry that number - the group
    /// is unknown, and <c>Category.Id</c> stopped being the place a Nayax id is read from. A product
    /// with no group at all is unchanged: it has nothing to resolve.</para>
    /// </summary>
    private static long? LocalCategoryId(
        long? nayaxProductGroupId, IReadOnlyDictionary<string, long> localCategoryIdByNayaxId) =>
        nayaxProductGroupId is long groupId
        && localCategoryIdByNayaxId.TryGetValue(NayaxExternalId.From(groupId), out var localCategoryId)
            ? localCategoryId
            : null;
}
