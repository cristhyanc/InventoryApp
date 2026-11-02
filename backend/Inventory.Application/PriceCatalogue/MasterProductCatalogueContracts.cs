using Inventory.Domain.PriceCatalogue;

namespace Inventory.Application.PriceCatalogue;

/// <summary>
/// The server-enforced bound on one page of the shared master-product catalogue (issue #1023).
///
/// <para>It is <see cref="Stock.StockHistoryPaging"/>'s rule, deliberately: a catalogue grows without
/// limit, so the size of a page is the server's decision and not the client's - a missing, zero or
/// negative page size becomes <see cref="DefaultPageSize"/>, anything above <see cref="MaxPageSize"/>
/// is clamped to it, and the size that was actually served is reported back in the response. A
/// bounded answer is more useful to the page than a refusal, and a client cannot ask this surface for
/// the whole catalogue in one response by naming a large enough number.</para>
/// </summary>
public static class MasterProductCataloguePaging
{
    /// <summary>The page size used when the client does not ask for one.</summary>
    public const int DefaultPageSize = 50;

    /// <summary>The largest page the server will serve, whatever the client asks for.</summary>
    public const int MaxPageSize = 200;

    /// <summary>The page number and page size the server will actually serve for a requested pair.</summary>
    public static (int Page, int PageSize) Resolve(int? page, int? pageSize) =>
        (page is > 0 ? page.Value : 1,
            pageSize is > 0 ? Math.Min(pageSize.Value, MaxPageSize) : DefaultPageSize);
}

/// <summary>
/// What the Super Admin Master products page asks for (issue #1023): one bounded page of the shared
/// catalogue, optionally narrowed by a search over the brand and the name.
///
/// <para><see cref="Page"/>/<see cref="PageSize"/> are what the client asked for;
/// <see cref="MasterProductCataloguePaging"/> decides what it gets. <see cref="Search"/> is the text a
/// person typed, matched as a substring of the brand or the name, and blank is the same as absent.</para>
/// </summary>
public sealed record MasterProductCatalogueQuery(
    string? Search = null,
    int? Page = null,
    int? PageSize = null);

/// <summary>
/// The resolved query the persistence port is given: a trimmed search term, and the requested page as
/// a bounded <see cref="Skip"/>/<see cref="Take"/>.
///
/// <para>The store applies these and nothing else - it decides no bound, no default and no sort, and
/// it adds no business predicate, because the tables it reads are platform tables that belong to no
/// business at all (AGENTS.md § Tenant ownership and data isolation).</para>
/// </summary>
public sealed record MasterProductCatalogueFilter(string? Search, int Skip, int Take);

/// <summary>
/// How many supplier listings one master product has in each match status (issue #1023).
///
/// <para>All four are reported, including <see cref="Rejected"/>: a refusal is kept rather than
/// deleted (<see cref="SupplierListingMatchStatus.Rejected"/>), and a catalogue page that hid the
/// refused listings would make a product with nothing but refusals look like a product nobody has
/// ever looked up.</para>
///
/// <para>These are counts of <em>supplier</em> listings - facts about suppliers' shelves. There is
/// deliberately no count of the businesses tracking the product: that count is about businesses, and
/// this surface publishes nothing about any (issue #928's own restraint, for the same reason).</para>
/// </summary>
public sealed record MasterProductListingCounts(
    int Proposed,
    int Confirmed,
    int NeedsReview,
    int Rejected)
{
    /// <summary>Every listing of the product, whatever its match status.</summary>
    public int Total => Proposed + Confirmed + NeedsReview + Rejected;
}

/// <summary>
/// One master product as the catalogue listing reports it (issue #1023): its key, the four values that
/// say what the product <em>is</em>, when it was created and last changed, and how many supplier
/// listings hang from it in each match status.
///
/// <para>The normalised key the database compares by is deliberately not published, for the reason the
/// supplier presets do not publish theirs: whether two descriptions are the same product is the
/// database's answer and not a decision any client makes.</para>
/// </summary>
public sealed record CataloguedMasterProduct(
    long Id,
    string Brand,
    string Name,
    decimal SizeValue,
    MasterProductSizeUnit SizeUnit,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    MasterProductListingCounts ListingCounts);

/// <summary>
/// What the store answers for one page: the products of the requested slice, and how many the search
/// matches in total - not how many were returned.
/// </summary>
public sealed record MasterProductCatalogueResult(
    IReadOnlyList<CataloguedMasterProduct> Products,
    int TotalCount);

/// <summary>
/// What a Super Admin submitted to add a master product to the shared catalogue (issue #1024): the
/// four values that say what the product is, exactly as they were typed.
///
/// <para>Unvalidated on purpose - the brand may be blank, the size may be negative and the unit may
/// be an integer no declared unit has, because a deserialized JSON number binds an arbitrary one.
/// <c>MasterProductPolicy</c> is what decides whether this is a description at all, and it does so
/// before anything is stored.</para>
/// </summary>
public sealed record MasterProductSubmission(
    string? Brand,
    string? Name,
    decimal SizeValue,
    MasterProductSizeUnit SizeUnit);

/// <summary>
/// What a Super Admin submitted to correct one master product (issue #1024): the same four values,
/// plus the <c>UpdatedAtUtc</c> the page they were editing had read.
///
/// <para><see cref="ExpectedUpdatedAtUtc"/> is the expected-value half of the write. It is the row's
/// own stored instant rather than a new column, and an edit that presents one the row no longer holds
/// is refused rather than applied, so two Super Admin tabs cannot overwrite each other silently
/// (<c>MasterProductEditPolicy</c>). It is nullable because a request may omit it, which is an
/// incomplete request rather than a stale one.</para>
/// </summary>
public sealed record MasterProductEditSubmission(
    string? Brand,
    string? Name,
    decimal SizeValue,
    MasterProductSizeUnit SizeUnit,
    DateTime? ExpectedUpdatedAtUtc);

/// <summary>
/// One validated correction as the persistence port receives it (issue #1024): the description that
/// passed every rule of <c>MasterProductPolicy</c>, and the instant the edit expects the row to carry.
///
/// <para>The expected instant travels with the description rather than beside it, because the
/// comparison and the write are one operation: the store reads the row, compares, and writes inside
/// one transaction.</para>
/// </summary>
public sealed record MasterProductEdit(
    NormalisedMasterProduct Description,
    DateTime ExpectedUpdatedAtUtc);

/// <summary>
/// One bounded page of the shared catalogue. <see cref="Page"/>/<see cref="PageSize"/> are the
/// resolved values the caller actually received, which may differ from what it requested.
/// </summary>
public sealed record MasterProductCataloguePage(
    IReadOnlyList<CataloguedMasterProduct> Products,
    int TotalCount,
    int Page,
    int PageSize)
{
    /// <summary>Whether products matching the search remain after this page.</summary>
    public bool HasMore => (long)Page * PageSize < TotalCount;
}

/// <summary>
/// The price one supplier listing currently publishes (issue #1023), as the catalogue detail reports
/// it: the pack price of the accepted reading the listing points at, with the currency and the tax
/// basis it was read on and the instant it was observed.
///
/// <para><strong>The basis and the instant travel with the amount, never without it.</strong> A pack
/// price GST-inclusive and the same number GST-exclusive are different figures, and a reading from
/// three months ago is not today's price - so this record carries the catalogue's own stored
/// semantics (<c>SupplierPriceObservationPolicy</c>) rather than a bare number a page would have to
/// guess about. Nothing here is divided, converted or rounded: a per-unit comparison is
/// <c>CheapestUnitOfferPolicy</c>'s work on its own endpoint, and this is a read of what was observed.</para>
///
/// <para>It is <see langword="null"/> for a listing that has never been checked successfully, which is
/// a real state and not a missing value.</para>
/// </summary>
public sealed record CataloguedObservedPrice(
    decimal PackPrice,
    string Currency,
    SupplierPriceBasis PriceBasis,
    DateTime ObservedAtUtc);

/// <summary>
/// One supplier listing of a master product as the catalogue detail reports it (issue #1023): which
/// supplier publishes it, the supplier's own id and wording for it, the pack it describes, what the
/// catalogue thinks of it, when it was last checked and what it currently costs.
///
/// <para>Every text value is the supplier's own wording, published as <strong>plain text</strong>:
/// nothing on this path renders, sanitises or interprets it, and the only link a page may follow is
/// <see cref="Url"/>.</para>
/// </summary>
public sealed record CataloguedSupplierListing(
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
    CataloguedObservedPrice? LatestPrice);

/// <summary>
/// One master product and its supplier listings (issue #1023), which is the whole of what the detail
/// read answers.
///
/// <para>Nothing about any business is here, in either direction: the master product carries no
/// business identity at all, a supplier listing is a fact about a supplier's shelf, and no mapping,
/// tracking count or business name is read to build this.</para>
/// </summary>
public sealed record MasterProductDetail(
    CataloguedMasterProduct Product,
    IReadOnlyList<CataloguedSupplierListing> Listings);
