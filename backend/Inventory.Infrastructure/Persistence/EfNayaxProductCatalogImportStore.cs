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
/// decide the same product is new (the later <c>SaveChanges</c> then fails on the filtered unique
/// index over <c>(BusinessId, ExternalProductId)</c>, which since issue #735 is what refuses a
/// duplicated identity in place of the primary key), and a local catalogue edit committed between
/// this import's read and its write is simply overwritten by whichever writer commits last. Keeping
/// the sequence in one adapter only keeps the
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
/// its key.</para>
///
/// <para><strong>A row it creates is keyed by the database, not by the provider</strong> (issue
/// #735, parent #329 decision A4). The import sets no <c>Id</c> at all: SQLite assigns it, and the
/// remote id it came from is recorded in the external id column beside it. Three consequences are
/// the point of the change. Two businesses can import the same catalogue, because neither row's key
/// is the operator's number and the external id is unique only per business. A local row of this
/// business that happens to hold that number is simply another product - the entry creates a new row
/// and leaves it untouched, where the previous shape had to refuse the import by name because the
/// key it would have created was taken. And a product's category is reached through the category
/// row, so a group this snapshot creates is resolved by tracking that row rather than by predicting
/// the key it is about to be given (see <c>AssignCategory</c>).</para>
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
        // this import already has in hand are the identity lookup (issue #745). The row beside each
        // external id is what makes it one - the row itself, not its key, because since issue #735 a
        // row this import creates has no key until SQLite assigns one. The text stays opaque: the one
        // thing this adapter assumes about it is that NayaxExternalId is what writes it.
        var recordedProductByNayaxId = localProducts
            .Where(product => product.ExternalProductId is not null)
            .ToDictionary(product => product.ExternalProductId!, product => product, StringComparer.Ordinal);
        var recordedCategoryByNayaxId = localCategories
            .Where(category => category.ExternalCategoryId is not null)
            .ToDictionary(category => category.ExternalCategoryId!, category => category, StringComparer.Ordinal);

        // The categories this snapshot creates, kept apart from the recorded ones for exactly one
        // reason: their keys do not exist yet, so a product in such a group is pointed at the row
        // itself (see AssignCategory) instead of at a number this import would have to predict.
        var createdCategoryByNayaxId = new Dictionary<string, Category>(StringComparer.Ordinal);

        // Every row the snapshot named - created or merely updated - ends the import carrying its
        // Nayax id (issue #732, as a column since #759). A row that already carries one is left
        // exactly as it is: it is the same id, and rewriting it would be the one way this import
        // could move an identifier between rows. A created row joins the lookup immediately, so a
        // product whose group this snapshot just created resolves to it, and a snapshot that repeated
        // an id neither creates a second row nor records the id twice.
        foreach (var category in import.Categories)
        {
            var externalId = NayaxExternalId.From(category.NayaxProductGroupId);

            // Only a group this business has recorded no category for becomes a new category; a
            // recorded one keeps its local name. A local category whose own key merely equals the
            // group id is some other category, and since issue #735 that is plainly a coincidence.
            if (recordedCategoryByNayaxId.ContainsKey(externalId)
                || createdCategoryByNayaxId.ContainsKey(externalId))
                continue;

            createdCategoryByNayaxId[externalId] = new Category
            {
                ExternalCategoryId = externalId,
                Name = category.Name,
                Description = category.Description,
            };
        }

        // Added before the products, so every category this import creates is tracked by the time a
        // product is pointed at one and EF orders the inserts accordingly.
        _db.Categories.AddRange(createdCategoryByNayaxId.Values);

        foreach (var entry in import.Products)
        {
            var externalId = NayaxExternalId.From(entry.NayaxProductId);
            if (!recordedProductByNayaxId.TryGetValue(externalId, out var product))
            {
                product = new Product
                {
                    ExternalProductId = externalId,
                    RestockTo = 0,
                    CreatedAt = import.ImportedAtUtc,
                };
                _db.Products.Add(product);
                recordedProductByNayaxId[externalId] = product;
            }

            // Only the Nayax-managed catalogue fields are written. Stock, costing and the
            // operator's own catalogue edits are local state this import does not own.
            product.Name = entry.Name;
            product.Description = entry.Description;
            product.UnitPrice = entry.UnitPrice;
            AssignCategory(
                product, entry.NayaxProductGroupId, recordedCategoryByNayaxId, createdCategoryByNayaxId);
            product.UpdatedAt = import.ImportedAtUtc;
        }

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
    /// Points a product at the local category its Nayax <c>ProductGroupID</c> names (issue #745).
    /// The Lynx contract declares that field as <c>integer&lt;int32&gt;</c>, nullable, "the unique
    /// identifier of the product group to which the product belongs" (<c>GET
    /// /v1/operators/{OperatorID}/products</c>), and the group endpoint issues ids from that same
    /// space (<c>GET /v1/operators/{ActorID}/productGroups</c>), which is what makes one column -
    /// <c>Category.ExternalCategoryId</c> - the answer for both.
    ///
    /// <para>A group this business has recorded on no category leaves the product uncategorised
    /// rather than pointing it at whichever local category happens to carry that number - the group
    /// is unknown, and <c>Category.Id</c> is not where a Nayax id is read from. A product with no
    /// group at all is unchanged: it has nothing to resolve.</para>
    ///
    /// <para>A recorded category is named by its foreign key. A category <em>this import</em>
    /// created has no key yet (issue #735), so the navigation is what names it and EF writes the key
    /// the insert assigns into <c>Product.CategoryId</c> - which is how a first import still
    /// categorises its products without the import ever choosing a key.</para>
    ///
    /// <para><strong>Both properties are written every time, including to clear them.</strong> A
    /// snapshot may name the same product twice, and the last occurrence decides its category as it
    /// decides its name and price. Writing only one of the pair would leave the other holding what
    /// an earlier occurrence put there - a navigation to a category this import is creating beside a
    /// foreign key that was since cleared or pointed elsewhere - and which of the two
    /// <c>SaveChanges</c> believes when they disagree is not something a financial write should
    /// depend on. So a recorded category is assigned to both, no resolvable group clears both, and a
    /// created one is assigned to the navigation with the foreign key left empty: that pair is the
    /// ordinary way to name a principal whose key does not exist yet, and it is the same state a
    /// product this import is inserting is in.</para>
    /// </summary>
    private static void AssignCategory(
        Product product,
        long? nayaxProductGroupId,
        IReadOnlyDictionary<string, Category> recordedCategoryByNayaxId,
        IReadOnlyDictionary<string, Category> createdCategoryByNayaxId)
    {
        var externalId = nayaxProductGroupId is long groupId ? NayaxExternalId.From(groupId) : null;

        if (externalId is not null && createdCategoryByNayaxId.TryGetValue(externalId, out var created))
        {
            product.Category = created;
            product.CategoryId = null;
            return;
        }

        var recorded = externalId is not null
            && recordedCategoryByNayaxId.TryGetValue(externalId, out var recordedCategory)
                ? recordedCategory
                : null;

        product.Category = recorded;
        product.CategoryId = recorded?.Id;
    }
}
