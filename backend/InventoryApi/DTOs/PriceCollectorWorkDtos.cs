using Inventory.Application.PriceCatalogue;
using Inventory.Domain.PriceCatalogue;
using Inventory.Domain.Suppliers;

namespace InventoryApi.DTOs;

/// <summary>
/// One supplier the collector is to search on this run, as
/// <c>GET /api/price-collector/work</c> reports it (issue #924, parent #817).
///
/// <para>The connector is its enum's number, as every other enum this API publishes is, because its
/// members name collection routes rather than values a person reads. The site is the supplier master
/// record's own website, which is what the JB Metro route talks to - a new JB Metro site is a new
/// supplier record rather than new code.</para>
/// </summary>
public sealed record PriceCollectorSupplierResponse(
    int SupplierPresetId,
    string Name,
    SupplierPriceConnector PriceConnector,
    string? PriceConnectorKey,
    string? SiteUrl,
    bool PricesIncludeGst)
{
    public static PriceCollectorSupplierResponse From(PriceCollectorSupplier supplier)
    {
        ArgumentNullException.ThrowIfNull(supplier);

        return new PriceCollectorSupplierResponse(
            supplier.SupplierPresetId,
            supplier.Name,
            supplier.PriceConnector,
            supplier.PriceConnectorKey,
            supplier.SiteUrl,
            supplier.PricesIncludeGst);
    }
}

/// <summary>
/// One issued piece of work (issue #924): the id a result row about this product and this supplier
/// must reference (#937), and the supplier it is for.
/// </summary>
public sealed record PriceCollectorWorkItemResponse(long WorkItemId, int SupplierPresetId)
{
    public static PriceCollectorWorkItemResponse From(PriceCollectorWorkItemHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);

        return new PriceCollectorWorkItemResponse(handle.WorkItemId, handle.SupplierPresetId);
    }
}

/// <summary>
/// One listing the catalogue already holds, so the collector can recognise it in a search result
/// (issue #924).
///
/// <para><strong>There is no price here.</strong> What a listing cost is an observation (#925), and
/// the collector is the thing that reports prices rather than the thing that is told them. What it
/// gets is the supplier's own id for the listing, the pack size the catalogue has confirmed, and when
/// the listing was last priced successfully.</para>
/// </summary>
public sealed record PriceCollectorKnownListingResponse(
    long ListingId,
    int SupplierPresetId,
    string SupplierProductId,
    string? Url,
    string? Title,
    int? UnitsPerPack,
    string? Barcode,
    BarcodeScope? BarcodeScope,
    DateTime? LastCheckedAtUtc)
{
    public static PriceCollectorKnownListingResponse From(PriceCollectorKnownListing listing)
    {
        ArgumentNullException.ThrowIfNull(listing);

        return new PriceCollectorKnownListingResponse(
            listing.ListingId,
            listing.SupplierPresetId,
            listing.SupplierProductId,
            listing.Url,
            listing.Title,
            listing.UnitsPerPack,
            listing.Barcode,
            listing.BarcodeScope,
            listing.LastCheckedAtUtc);
    }
}

/// <summary>
/// One master product to price on this run, with its known listings and its work (issue #924).
///
/// <para>One entry per master product, however many businesses track it, and the product is a shared
/// description: a brand, a name and a size. No business id, no business name, no business product id
/// and no count of how many wanted it.</para>
///
/// <para><c>knownListingsOmitted</c> is how a bound states itself: a product holding more listings
/// than one run may carry is published with <strong>none</strong> of them and this flag set, so an
/// empty list is never read as "this product has no listings". The product is still issued its work.</para>
/// </summary>
public sealed record PriceCollectorMatchedProductResponse(
    long MasterProductId,
    string Brand,
    string Name,
    decimal SizeValue,
    MasterProductSizeUnit SizeUnit,
    IReadOnlyList<PriceCollectorKnownListingResponse> KnownListings,
    bool KnownListingsOmitted,
    IReadOnlyList<PriceCollectorWorkItemResponse> WorkItems)
{
    public static PriceCollectorMatchedProductResponse From(PriceCollectorMatchedProduct product)
    {
        ArgumentNullException.ThrowIfNull(product);

        return new PriceCollectorMatchedProductResponse(
            product.MasterProductId,
            product.Brand,
            product.Name,
            product.SizeValue,
            product.SizeUnit,
            [.. product.KnownListings.Select(PriceCollectorKnownListingResponse.From)],
            product.KnownListingsOmitted,
            [.. product.WorkItems.Select(PriceCollectorWorkItemResponse.From)]);
    }
}

/// <summary>
/// One product still waiting for a master product, as the collector is to search for it (issue #924).
///
/// <para>The search name and the unit label are the stored text several businesses' spellings folded
/// to, and the waiting key is that fold - which is the whole reason one product is one search. The
/// refused master products are the ones a Super Admin rejected or undid for this key, sent so a
/// matcher does not propose them again.</para>
///
/// <para><c>matchingSuppressed</c> is the one flag with a <strong>rule</strong> attached: it says the
/// refusals for this key exceeded what one run may carry, so none of them is above and
/// <em>no master product may be proposed or applied for this key on this run</em>. Half a refusal
/// list would let a pair somebody rejected be proposed again, which is the one thing the refusals
/// exist to prevent. The search itself is still issued. <c>proposedListingsOmitted</c> is the
/// ordinary size bound, as on a matched product.</para>
/// </summary>
public sealed record PriceCollectorWaitingProductResponse(
    string WaitingKey,
    string SearchName,
    string UnitLabel,
    IReadOnlyList<PriceCollectorKnownListingResponse> ProposedListings,
    bool ProposedListingsOmitted,
    IReadOnlyList<long> RefusedMasterProductIds,
    bool MatchingSuppressed,
    IReadOnlyList<PriceCollectorWorkItemResponse> WorkItems)
{
    public static PriceCollectorWaitingProductResponse From(PriceCollectorWaitingProduct product)
    {
        ArgumentNullException.ThrowIfNull(product);

        return new PriceCollectorWaitingProductResponse(
            product.WaitingKey,
            product.SearchName,
            product.UnitLabel,
            [.. product.ProposedListings.Select(PriceCollectorKnownListingResponse.From)],
            product.ProposedListingsOmitted,
            product.RefusedMasterProductIds,
            product.MatchingSuppressed,
            [.. product.WorkItems.Select(PriceCollectorWorkItemResponse.From)]);
    }
}

/// <summary>
/// One master product a waiting product may be matched against (issue #924, for #941): its id and
/// its description, and deliberately nothing else.
/// </summary>
public sealed record PriceCollectorMasterProductResponse(
    long Id,
    string Brand,
    string Name,
    decimal SizeValue,
    MasterProductSizeUnit SizeUnit)
{
    public static PriceCollectorMasterProductResponse From(PriceCollectorMasterProduct master)
    {
        ArgumentNullException.ThrowIfNull(master);

        return new PriceCollectorMasterProductResponse(
            master.Id,
            master.Brand,
            master.Name,
            master.SizeValue,
            master.SizeUnit);
    }
}

/// <summary>
/// The whole work list as <c>GET /api/price-collector/work</c> answers it (issue #924, parent #817).
///
/// <para><strong>This contract carries no business data of any kind</strong> - no business id, no
/// business name, no business product id and no price - which is the issue's own criterion stated as
/// a type rather than as a promise. Every product in it is either a shared master product's
/// description or the folded search text of one nothing has matched yet.</para>
///
/// <para><strong>It is bounded, and the bound is a schedule.</strong>
/// <paramref name="WorkItemLimitReached"/> says the catalogue held more work than one run may be
/// issued with, so a collector knows it has a deterministic stretch of the product rotation rather
/// than everything there is to search - and the next run is issued the stretch after it, so the
/// remainder is searched on later runs. What travels under each published product is bounded per
/// product, and a product whose listings or refusals were omitted says so.</para>
/// </summary>
/// <param name="RunId">The run the collector reports its results under (#937).</param>
/// <param name="IssuedAtUtc">When the run was issued.</param>
/// <param name="ExpiresAtUtc">
/// When the run stops being open unless the collector reports back. A later run expires this one
/// whatever this instant says.
/// </param>
/// <param name="Suppliers">The suppliers due on this run, with the route to each.</param>
/// <param name="MatchedProducts">The master products to price, with their known listings.</param>
/// <param name="WaitingProducts">The products to search for by name.</param>
/// <param name="MasterProductsForMatching">
/// The master products a waiting product may be matched against. Empty unless something is actually
/// waiting.
/// </param>
/// <param name="WorkItemLimitReached">
/// Whether more work was due than this run was issued with. The products this run omitted are the
/// next run's first, so the flag says "there is more, and it is coming" rather than "the rest is
/// lost".
/// </param>
public sealed record PriceCollectorWorkResponse(
    long RunId,
    DateTime IssuedAtUtc,
    DateTime ExpiresAtUtc,
    IReadOnlyList<PriceCollectorSupplierResponse> Suppliers,
    IReadOnlyList<PriceCollectorMatchedProductResponse> MatchedProducts,
    IReadOnlyList<PriceCollectorWaitingProductResponse> WaitingProducts,
    IReadOnlyList<PriceCollectorMasterProductResponse> MasterProductsForMatching,
    bool WorkItemLimitReached)
{
    public static PriceCollectorWorkResponse From(PriceCollectorWorkList workList)
    {
        ArgumentNullException.ThrowIfNull(workList);

        return new PriceCollectorWorkResponse(
            workList.RunId,
            workList.IssuedAtUtc,
            workList.ExpiresAtUtc,
            [.. workList.Suppliers.Select(PriceCollectorSupplierResponse.From)],
            [.. workList.MatchedProducts.Select(PriceCollectorMatchedProductResponse.From)],
            [.. workList.WaitingProducts.Select(PriceCollectorWaitingProductResponse.From)],
            [.. workList.MasterProductsForMatching.Select(PriceCollectorMasterProductResponse.From)],
            workList.WorkItemLimitReached);
    }
}
