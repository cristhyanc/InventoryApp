using Inventory.Domain.PriceCatalogue;
using Inventory.Domain.Suppliers;

namespace Inventory.Application.PriceCatalogue;

/// <summary>
/// One supplier the collector is to search on this run (issue #924): what it is called, how to reach
/// it, and what its published prices mean.
///
/// <para>It carries the supplier master record's own collection fields of #931 and nothing more. No
/// credential, no cadence and no business: the connector is a route name, the key says which
/// retailer that route is for, and the site URL is what the JB Metro route talks to.</para>
/// </summary>
public sealed record PriceCollectorSupplier(
    int SupplierPresetId,
    string Name,
    SupplierPriceConnector PriceConnector,
    string? PriceConnectorKey,
    string? SiteUrl,
    bool PricesIncludeGst);

/// <summary>One issued piece of work as the work list publishes it (issue #924).</summary>
/// <param name="WorkItemId">
/// The id every result row about this product and this supplier must reference (#937).
/// </param>
/// <param name="SupplierPresetId">The supplier this piece of work is for.</param>
public sealed record PriceCollectorWorkItemHandle(long WorkItemId, int SupplierPresetId);

/// <summary>
/// One listing the catalogue already holds, as the collector reads it in order to recognise a listing
/// it has seen before (issue #924).
///
/// <para>Deliberately not the listing's whole row: no match status provenance, no barcode scope
/// reasoning and no price. What a collector needs is enough to recognise the listing in a search
/// result - the supplier's own id for it, the pack size the catalogue has confirmed, and when it was
/// last priced.</para>
/// </summary>
public sealed record PriceCollectorKnownListing(
    long ListingId,
    int SupplierPresetId,
    string SupplierProductId,
    string? Url,
    string? Title,
    int? UnitsPerPack,
    string? Barcode,
    BarcodeScope? BarcodeScope,
    DateTime? LastCheckedAtUtc);

/// <summary>
/// One master product the collector is to price on this run, with the listings the catalogue already
/// holds for it and the work issued against it (issue #924).
///
/// <para><strong>One entry per master product, however many businesses track it.</strong> That is the
/// parent issue's whole outcome, and it is why this is a description rather than anybody's product:
/// no business id, no business name, no business product id and no count of how many wanted it.</para>
/// </summary>
public sealed record PriceCollectorMatchedProduct(
    long MasterProductId,
    string Brand,
    string Name,
    decimal SizeValue,
    MasterProductSizeUnit SizeUnit,
    IReadOnlyList<PriceCollectorKnownListing> KnownListings,
    IReadOnlyList<PriceCollectorWorkItemHandle> WorkItems);

/// <summary>
/// One product still waiting for a master product, as the collector is to search for it (issue #924):
/// the search text, the key an answer belongs to, what a previous run already found under that key,
/// and the master products a Super Admin has refused for it.
///
/// <para>It carries no business identity of any kind. The search name and unit label are the stored
/// text several businesses' spellings folded to, and the waiting key is that fold - which is the
/// whole reason one product is one search.</para>
/// </summary>
public sealed record PriceCollectorWaitingProduct(
    string WaitingKey,
    string SearchName,
    string UnitLabel,
    IReadOnlyList<PriceCollectorKnownListing> ProposedListings,
    IReadOnlyList<long> RefusedMasterProductIds,
    IReadOnlyList<PriceCollectorWorkItemHandle> WorkItems);

/// <summary>
/// One master product a waiting product may be matched against (issue #924, for #941): its id and its
/// description, and nothing else.
/// </summary>
public sealed record PriceCollectorMasterProduct(
    long Id,
    string Brand,
    string Name,
    decimal SizeValue,
    MasterProductSizeUnit SizeUnit);

/// <summary>
/// One issued collector work list (issue #924, parent #817): a run id, the suppliers due on it, the
/// products to search, the work issued against each of them, and - only while something is waiting -
/// the master products to match against.
///
/// <para><strong>Nothing here is a business's.</strong> No business id, no business name, no business
/// product id and no price appears anywhere in it, which is the issue's own criterion stated as a
/// type: every product is either a shared master product's description or the folded search text of
/// one that has not been matched yet.</para>
///
/// <para><strong>It is bounded.</strong> <see cref="WorkItemLimitReached"/> says the catalogue held
/// more work than one run may be issued with, so a collector knows it has a deterministic prefix
/// rather than everything there is to search (<see cref="PriceCollectorWorkListPolicy"/>).</para>
/// </summary>
public sealed record PriceCollectorWorkList(
    long RunId,
    DateTime IssuedAtUtc,
    DateTime ExpiresAtUtc,
    IReadOnlyList<PriceCollectorSupplier> Suppliers,
    IReadOnlyList<PriceCollectorMatchedProduct> MatchedProducts,
    IReadOnlyList<PriceCollectorWaitingProduct> WaitingProducts,
    IReadOnlyList<PriceCollectorMasterProduct> MasterProductsForMatching,
    bool WorkItemLimitReached);
