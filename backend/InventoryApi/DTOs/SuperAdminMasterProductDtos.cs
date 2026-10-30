using Inventory.Application.PriceCatalogue;
using Inventory.Domain.PriceCatalogue;
using Microsoft.AspNetCore.Mvc;

namespace InventoryApi.DTOs;

/// <summary>
/// The query string of <c>GET /api/super-admin/master-products</c> (issue #1023), bound as one object
/// rather than as three scalar parameters, the grouping <see cref="StockHistoryRequest"/> uses.
///
/// <para>Property declaration order is the published parameter order, and each property keeps its
/// camelCase query-string name through <see cref="FromQueryAttribute.Name"/>, because ASP.NET Core
/// would otherwise publish and bind the PascalCase C# name. Nothing here decides what a value means:
/// the bound is <see cref="MasterProductCataloguePaging"/>'s and the search is the store's.</para>
/// </summary>
public sealed record SuperAdminMasterProductsRequest(
    [property: FromQuery(Name = "search")] string? Search = null,
    [property: FromQuery(Name = "page")] int? Page = null,
    [property: FromQuery(Name = "pageSize")] int? PageSize = null);

/// <summary>
/// How many supplier listings one master product has in each match status (issue #1023), as the
/// Super Admin catalogue publishes them.
///
/// <para>Four counts of <em>supplier</em> listings and nothing else: there is deliberately no count of
/// the businesses tracking the product, because that count is about businesses and this surface
/// publishes nothing about any (the owner's clarification of 31 October 2026).</para>
/// </summary>
public sealed record SuperAdminMasterProductListingCountsResponse(
    int Proposed,
    int Confirmed,
    int NeedsReview,
    int Rejected,
    int Total)
{
    public static SuperAdminMasterProductListingCountsResponse From(MasterProductListingCounts counts)
    {
        ArgumentNullException.ThrowIfNull(counts);

        return new SuperAdminMasterProductListingCountsResponse(
            counts.Proposed,
            counts.Confirmed,
            counts.NeedsReview,
            counts.Rejected,
            counts.Total);
    }
}

/// <summary>
/// One master product as the Super Admin catalogue publishes it (issue #1023): its key, the four
/// values that say what the product is, both of its instants, and how its supplier listings stand.
///
/// <para>It is a separate contract from <see cref="SuperAdminReviewMasterProductResponse"/> rather
/// than a reuse of it, because the two answer different questions: the review publishes the four
/// description values so a reviewer can pick a product, and this one adds the instants and the
/// listing counts a catalogue page is read from. Neither carries the normalised key the database
/// compares by, and neither carries anything about a business.</para>
/// </summary>
public sealed record SuperAdminMasterProductResponse(
    long Id,
    string Brand,
    string Name,
    decimal SizeValue,
    MasterProductSizeUnit SizeUnit,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    SuperAdminMasterProductListingCountsResponse ListingCounts)
{
    public static SuperAdminMasterProductResponse From(CataloguedMasterProduct product)
    {
        ArgumentNullException.ThrowIfNull(product);

        return new SuperAdminMasterProductResponse(
            product.Id,
            product.Brand,
            product.Name,
            product.SizeValue,
            product.SizeUnit,
            product.CreatedAtUtc,
            product.UpdatedAtUtc,
            SuperAdminMasterProductListingCountsResponse.From(product.ListingCounts));
    }
}

/// <summary>
/// One bounded page of the shared catalogue (issue #1023): the products of this slice, how many the
/// search matched in total, and which page at which size was actually served.
///
/// <para><see cref="Page"/>/<see cref="PageSize"/> are the <em>served</em> values, which may differ
/// from what the client asked for - a page size above the maximum is bounded rather than refused, and
/// a client that was not told would not know which slice it was looking at.</para>
/// </summary>
public sealed record SuperAdminMasterProductPageResponse(
    IReadOnlyList<SuperAdminMasterProductResponse> Products,
    int TotalCount,
    int Page,
    int PageSize,
    bool HasMore)
{
    public static SuperAdminMasterProductPageResponse From(MasterProductCataloguePage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        return new SuperAdminMasterProductPageResponse(
            [.. page.Products.Select(SuperAdminMasterProductResponse.From)],
            page.TotalCount,
            page.Page,
            page.PageSize,
            page.HasMore);
    }
}

/// <summary>
/// What one supplier listing currently costs (issue #1023): the pack price of the accepted reading the
/// listing points at, with the currency and the tax basis it was read on and the instant it was
/// observed.
///
/// <para>The basis and the instant are published with the amount and never without it, because a pack
/// price GST-inclusive and the same number GST-exclusive are different figures and a reading from
/// three months ago is not today's price. Nothing here is a per-unit figure: that comparison is the
/// cheapest-supplier summary's, on its own endpoint and its own rule.</para>
/// </summary>
public sealed record SuperAdminMasterProductPriceResponse(
    decimal PackPrice,
    string Currency,
    SupplierPriceBasis PriceBasis,
    DateTime ObservedAtUtc)
{
    public static SuperAdminMasterProductPriceResponse From(CataloguedObservedPrice price)
    {
        ArgumentNullException.ThrowIfNull(price);

        return new SuperAdminMasterProductPriceResponse(
            price.PackPrice,
            price.Currency,
            price.PriceBasis,
            price.ObservedAtUtc);
    }
}

/// <summary>
/// One supplier listing of a master product as the Super Admin catalogue publishes it (issue #1023):
/// the supplier's own name, its product id and wording, the pack it describes, what the catalogue
/// thinks of it, when it was last checked and what it currently costs.
///
/// <para>Every text value is the supplier's own wording, published as <strong>plain text</strong>:
/// nothing on this path renders, sanitises or interprets it, and the only link a page may offer is
/// <see cref="Url"/>. <see cref="LatestPrice"/> is <see langword="null"/> for a listing that has never
/// been checked successfully, which is a state and not a missing value.</para>
/// </summary>
public sealed record SuperAdminMasterProductListingResponse(
    long Id,
    string SupplierName,
    string SupplierProductId,
    string? Title,
    decimal? SizeValue,
    MasterProductSizeUnit? SizeUnit,
    int? UnitsPerPack,
    SupplierListingMatchStatus MatchStatus,
    string? Url,
    DateTime? LastCheckedAtUtc,
    SuperAdminMasterProductPriceResponse? LatestPrice)
{
    public static SuperAdminMasterProductListingResponse From(CataloguedSupplierListing listing)
    {
        ArgumentNullException.ThrowIfNull(listing);

        return new SuperAdminMasterProductListingResponse(
            listing.Id,
            listing.SupplierName,
            listing.SupplierProductId,
            listing.Title,
            listing.SizeValue,
            listing.SizeUnit,
            listing.UnitsPerPack,
            listing.MatchStatus,
            listing.Url,
            listing.LastCheckedAtUtc,
            listing.LatestPrice is null
                ? null
                : SuperAdminMasterProductPriceResponse.From(listing.LatestPrice));
    }
}

/// <summary>
/// One master product and its supplier listings (issue #1023), which is the whole of what
/// <c>GET /api/super-admin/master-products/{id}</c> answers.
/// </summary>
public sealed record SuperAdminMasterProductDetailResponse(
    SuperAdminMasterProductResponse Product,
    IReadOnlyList<SuperAdminMasterProductListingResponse> Listings)
{
    public static SuperAdminMasterProductDetailResponse From(MasterProductDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);

        return new SuperAdminMasterProductDetailResponse(
            SuperAdminMasterProductResponse.From(detail.Product),
            [.. detail.Listings.Select(SuperAdminMasterProductListingResponse.From)]);
    }
}
