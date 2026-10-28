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
/// <param name="MasterProductId">The master product's own key.</param>
/// <param name="Brand">Its brand.</param>
/// <param name="Name">Its name or variant.</param>
/// <param name="SizeValue">The size of one item.</param>
/// <param name="SizeUnit">What that size is measured in.</param>
/// <param name="KnownListings">
/// The listings the catalogue holds for it, at most
/// <see cref="PriceCollectorWorkListPolicy.MaxListingsPerProduct"/> of them, and none at all when it
/// holds more than that - see <paramref name="KnownListingsOmitted"/>.
/// </param>
/// <param name="KnownListingsOmitted">
/// Whether this product holds more listings than one run may carry, so <em>none</em> of them is
/// above. The product is still issued its work: what is lost is the recognition aid, so a listing the
/// collector reports is reconciled by <c>(supplier master record, supplier product id)</c> rather
/// than recognised, and the omission is stated rather than read as "this product has no listings".
/// </param>
/// <param name="WorkItems">The work issued against it, one per due supplier.</param>
public sealed record PriceCollectorMatchedProduct(
    long MasterProductId,
    string Brand,
    string Name,
    decimal SizeValue,
    MasterProductSizeUnit SizeUnit,
    IReadOnlyList<PriceCollectorKnownListing> KnownListings,
    bool KnownListingsOmitted,
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
/// <param name="WaitingKey">The key the search name and unit label below fold to.</param>
/// <param name="SearchName">The product name to search for.</param>
/// <param name="UnitLabel">The unit label beside it, and the empty string where there is none.</param>
/// <param name="ProposedListings">
/// What a previous run already found under this key, bounded exactly as a matched product's known
/// listings are.
/// </param>
/// <param name="ProposedListingsOmitted">
/// Whether this key holds more proposals than one run may carry, so none of them is above.
/// </param>
/// <param name="RefusedMasterProductIds">
/// The master products a Super Admin rejected or undid for this key, so a matcher does not propose
/// them again - all of them, or none at all when there are more than one run may carry.
/// </param>
/// <param name="MatchingSuppressed">
/// Whether the refusals were omitted, which means <strong>no master product may be proposed or
/// applied for this key on this run</strong>: the refusal list is the safeguard that stops a rejected
/// pair being proposed a second time, and half of it is not a safeguard. The search itself is still
/// issued - what it finds is recorded under the key as a proposal - and the pairing is left to a run
/// whose refusals fit, or to a person who clears some.
/// </param>
/// <param name="WorkItems">The work issued against it, one per due supplier.</param>
public sealed record PriceCollectorWaitingProduct(
    string WaitingKey,
    string SearchName,
    string UnitLabel,
    IReadOnlyList<PriceCollectorKnownListing> ProposedListings,
    bool ProposedListingsOmitted,
    IReadOnlyList<long> RefusedMasterProductIds,
    bool MatchingSuppressed,
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
/// <para><strong>It is bounded, and the bound is a schedule.</strong>
/// <see cref="WorkItemLimitReached"/> says the catalogue held more work than one run may be issued
/// with, so a collector knows it has a deterministic stretch of the rotation rather than everything
/// there is to search - and the next run is issued the stretch after it, so the remainder is searched
/// on later runs rather than never (<see cref="PriceCollectorWorkListPolicy"/>). What travels under
/// each published product is bounded per product, and a product whose context was omitted says
/// so.</para>
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
