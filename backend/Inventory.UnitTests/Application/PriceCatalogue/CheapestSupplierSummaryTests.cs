using Inventory.Application.PriceCatalogue;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Gst;
using Inventory.Domain.PriceCatalogue;
using Inventory.Domain.Tenancy;
using InventoryApi.Tests.Application.Gst;
using InventoryApi.Tests.Application.Time;
using Xunit;

namespace Inventory.UnitTests.Application.PriceCatalogue;

/// <summary>
/// The cheapest-supplier summary use case (issue #930, parent #817): the request bound, the GST
/// basis the business is compared on, ties, the ineligibility reasons passed through, and the states
/// that are not prices - a supplier that does not list the product, a listing nobody has checked, and
/// a product nobody has matched.
///
/// <para>Over a fake store, because what is under test is the arrangement of
/// <c>CheapestUnitOfferPolicy</c>'s answer and nothing relational: the SQL, the tenant filters and
/// the platform-table join are <c>EfCheapestSupplierSummaryStoreTests</c>, and the HTTP contract is
/// <c>CheapestSupplierSummaryApiTests</c>.</para>
///
/// <para>Every expected price here is written as the arithmetic the policy does - <c>22.80m / 24</c>
/// rather than <c>0.95</c> - so a test can never pin a figure this application rounded or a figure a
/// person worked out by hand.</para>
/// </summary>
public class CheapestSupplierSummaryTests
{
    private static readonly DateTime Now = new(2026, 10, 28, 6, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Observed = Now.AddHours(-2);

    private const int JbMetroPreset = 11;
    private const int ColesPreset = 12;
    private const int WoolworthsPreset = 13;

    private static readonly LinkedSupplier JbMetro = new(1, "JB Metro", JbMetroPreset);
    private static readonly LinkedSupplier Coles = new(2, "Coles", ColesPreset);
    private static readonly LinkedSupplier Woolworths = new(3, "Woolworths", WoolworthsPreset);

    #region What a request may ask for

    /// <summary>The criterion: an empty list is refused, and nothing is read.</summary>
    [Fact]
    public async Task A_request_for_no_products_is_refused()
    {
        var store = new FakeStore();

        var refusal = await Assert.ThrowsAsync<DomainValidationException>(
            () => Summary(store).Handle([], CancellationToken.None));

        Assert.Equal(CheapestSupplierSummaryRequestPolicy.NoProductIdsMessage, refusal.Message);
        Assert.False(store.WasRead);
    }

    /// <summary>And so is a request with more ids than the agreed batch size.</summary>
    [Fact]
    public async Task A_request_for_more_than_the_batch_size_is_refused()
    {
        var store = new FakeStore();
        var tooMany = Enumerable
            .Range(1, CheapestSupplierSummaryRequestPolicy.MaxProductIds + 1)
            .Select(id => (long)id)
            .ToList();

        var refusal = await Assert.ThrowsAsync<DomainValidationException>(
            () => Summary(store).Handle(tooMany, CancellationToken.None));

        Assert.Equal(CheapestSupplierSummaryRequestPolicy.TooManyProductIdsMessage, refusal.Message);
        Assert.False(store.WasRead);
    }

    /// <summary>Exactly the batch size is accepted: the bound is a limit, not a margin.</summary>
    [Fact]
    public async Task A_request_for_the_whole_batch_size_is_accepted()
    {
        var store = new FakeStore();
        var full = Enumerable
            .Range(1, CheapestSupplierSummaryRequestPolicy.MaxProductIds)
            .Select(id => (long)id)
            .ToList();

        await Summary(store).Handle(full, CancellationToken.None);

        Assert.Equal(full, store.RequestedIds);
    }

    /// <summary>
    /// The criterion: a duplicate id is one question. It is read once, and it never costs the caller
    /// part of its allowance - a page listing one product twice is still one page.
    /// </summary>
    [Fact]
    public async Task A_repeated_id_is_read_once()
    {
        var store = new FakeStore();

        await Summary(store).Handle([7, 7, 9, 7], CancellationToken.None);

        Assert.Equal([7, 9], store.RequestedIds);
    }

    #endregion

    #region The basis the business is compared on

    /// <summary>
    /// The criterion: a GST-registered business is shown and compared ex-GST, because that is what a
    /// registered business actually pays for a can. Both figures are still published.
    /// </summary>
    [Fact]
    public async Task A_gst_registered_business_is_compared_on_the_ex_gst_price()
    {
        var store = Store(gstRegistered: true, [JbMetro], Matched(7, [Listing(JbMetroPreset, 24, 22.80m, SupplierPriceBasis.InclusiveOfGst)]));

        var summary = await Summary(store).Handle([7], CancellationToken.None);

        Assert.Equal(SupplierPriceBasis.ExclusiveOfGst, summary.DisplayBasis);

        var offer = Assert.Single(summary.Products[0].Suppliers);
        Assert.Equal(22.80m / 24 / 1.1m, offer.UnitPrice);
        Assert.Equal(22.80m / 24 / 1.1m, offer.UnitPriceExGst);
        Assert.Equal(22.80m / 24, offer.UnitPriceIncGst);
        Assert.Equal(offer.UnitPrice, summary.Products[0].CheapestUnitPrice);
    }

    /// <summary>And a business that is not registered is shown the price it hands over.</summary>
    [Fact]
    public async Task A_business_that_is_not_registered_is_compared_on_the_inc_gst_price()
    {
        var store = Store(gstRegistered: false, [JbMetro], Matched(7, [Listing(JbMetroPreset, 24, 22.80m, SupplierPriceBasis.InclusiveOfGst)]));

        var summary = await Summary(store).Handle([7], CancellationToken.None);

        Assert.Equal(SupplierPriceBasis.InclusiveOfGst, summary.DisplayBasis);
        Assert.Equal(22.80m / 24, summary.Products[0].Suppliers[0].UnitPrice);
        Assert.Equal(22.80m / 24, summary.Products[0].CheapestUnitPrice);
    }

    #endregion

    #region The comparison itself

    /// <summary>
    /// The cheapest is the smallest unit price on the business's own basis, and the offers are
    /// published cheapest first - the policy's order, not a second one arranged here.
    /// </summary>
    [Fact]
    public async Task The_cheapest_offer_is_marked_and_published_first()
    {
        var store = Store(
            gstRegistered: false,
            [JbMetro, Coles],
            Matched(7,
            [
                Listing(ColesPreset, 1, 1.27m, SupplierPriceBasis.InclusiveOfGst),
                Listing(JbMetroPreset, 24, 22.80m, SupplierPriceBasis.InclusiveOfGst),
            ]));

        var product = (await Summary(store).Handle([7], CancellationToken.None)).Products[0];

        Assert.Equal(["JB Metro", "Coles"], product.Suppliers.Select(offer => offer.SupplierName));
        Assert.Equal([true, false], product.Suppliers.Select(offer => offer.IsCheapest));
        Assert.Equal(22.80m / 24, product.CheapestUnitPrice);
    }

    /// <summary>
    /// The criterion: a tie is one answer with two suppliers in it. Picking one of them would invent
    /// a preference nobody stated.
    /// </summary>
    [Fact]
    public async Task Every_supplier_in_a_tie_is_marked_cheapest()
    {
        var store = Store(
            gstRegistered: false,
            [JbMetro, Coles],
            Matched(7,
            [
                Listing(JbMetroPreset, 10, 12.50m, SupplierPriceBasis.InclusiveOfGst),
                Listing(ColesPreset, 2, 2.50m, SupplierPriceBasis.InclusiveOfGst),
            ]));

        var product = (await Summary(store).Handle([7], CancellationToken.None)).Products[0];

        Assert.All(product.Suppliers, offer => Assert.True(offer.IsCheapest));
        Assert.Equal(1.25m, product.CheapestUnitPrice);
    }

    /// <summary>
    /// The criterion: every ineligibility reason the policy decides is passed through, and the offer
    /// is still published - an owner who cannot see why a supplier is out of the comparison will
    /// wonder why it is missing.
    /// </summary>
    [Theory]
    [InlineData(SupplierOfferIneligibility.PackSizeUnknown)]
    [InlineData(SupplierOfferIneligibility.ListingNotConfirmed)]
    [InlineData(SupplierOfferIneligibility.GstRateUnknown)]
    [InlineData(SupplierOfferIneligibility.OutOfStock)]
    [InlineData(SupplierOfferIneligibility.PromotionEnded)]
    [InlineData(SupplierOfferIneligibility.Stale)]
    public async Task An_offer_that_cannot_be_compared_is_published_with_its_reason(
        SupplierOfferIneligibility reason)
    {
        var listing = reason switch
        {
            SupplierOfferIneligibility.PackSizeUnknown =>
                Listing(JbMetroPreset, null, 22.80m, SupplierPriceBasis.InclusiveOfGst),
            SupplierOfferIneligibility.ListingNotConfirmed =>
                Listing(JbMetroPreset, 24, 22.80m, SupplierPriceBasis.InclusiveOfGst) with
                {
                    ListingConfirmed = false,
                },
            SupplierOfferIneligibility.GstRateUnknown =>
                Listing(JbMetroPreset, 24, 22.80m, SupplierPriceBasis.InclusiveOfGst),
            SupplierOfferIneligibility.OutOfStock =>
                WithPrice(
                    Listing(JbMetroPreset, 24, 22.80m, SupplierPriceBasis.InclusiveOfGst),
                    price => price with { StockStatus = SupplierStockStatus.OutOfStock }),
            SupplierOfferIneligibility.PromotionEnded =>
                WithPrice(
                    Listing(JbMetroPreset, 24, 22.80m, SupplierPriceBasis.InclusiveOfGst),
                    price => price with
                    {
                        IsPromotion = true,
                        PromotionEndsAtUtc = Now.AddHours(-1),
                    }),
            _ => WithPrice(
                Listing(JbMetroPreset, 24, 22.80m, SupplierPriceBasis.InclusiveOfGst),
                price => price with { ObservedAtUtc = Now.AddHours(-100) }),
        };

        // The unknown-rate case is the one that needs the product's rule withheld as well: the
        // supplier's own rate is what the other cases carry.
        var rule = reason == SupplierOfferIneligibility.GstRateUnknown
            ? GstRules.None
            : GstClassification.Taxable;
        var withoutSupplierRate = reason == SupplierOfferIneligibility.GstRateUnknown
            ? WithPrice(listing, price => price with { SupplierGstRate = null })
            : listing;

        var store = Store(
            gstRegistered: false,
            [JbMetro],
            Matched(7, [withoutSupplierRate], rule));

        var product = (await Summary(store).Handle([7], CancellationToken.None)).Products[0];

        var offer = Assert.Single(product.Suppliers);
        Assert.Equal(reason, offer.Ineligibility);
        Assert.False(offer.IsCheapest);
        Assert.Null(product.CheapestUnitPrice);
        Assert.True(offer.HasPrice);
    }

    /// <summary>
    /// A comparable offer and one that is not: the comparison still has an answer, and the offer that
    /// could not take part is published after it rather than dropped.
    /// </summary>
    [Fact]
    public async Task An_ineligible_offer_is_published_after_the_comparable_ones()
    {
        var store = Store(
            gstRegistered: false,
            [JbMetro, Coles],
            Matched(7,
            [
                WithPrice(
                    Listing(ColesPreset, 1, 0.50m, SupplierPriceBasis.InclusiveOfGst),
                    price => price with { StockStatus = SupplierStockStatus.OutOfStock }),
                Listing(JbMetroPreset, 24, 22.80m, SupplierPriceBasis.InclusiveOfGst),
            ]));

        var product = (await Summary(store).Handle([7], CancellationToken.None)).Products[0];

        Assert.Equal(["JB Metro", "Coles"], product.Suppliers.Select(offer => offer.SupplierName));
        Assert.Equal(22.80m / 24, product.CheapestUnitPrice);
        Assert.Equal(SupplierOfferIneligibility.OutOfStock, product.Suppliers[1].Ineligibility);
    }

    #endregion

    #region The states that are not prices

    /// <summary>
    /// The criterion's "Woolworths: no match": a linked, not-hidden supplier that does not list the
    /// product is published as itself with nothing else, rather than left out of the popup.
    /// </summary>
    [Fact]
    public async Task A_supplier_that_does_not_list_the_product_is_published_as_no_match()
    {
        var store = Store(
            gstRegistered: false,
            [JbMetro, Woolworths],
            Matched(7, [Listing(JbMetroPreset, 24, 22.80m, SupplierPriceBasis.InclusiveOfGst)]));

        var product = (await Summary(store).Handle([7], CancellationToken.None)).Products[0];

        var woolworths = Assert.Single(product.Suppliers, offer => offer.SupplierName == "Woolworths");
        Assert.False(woolworths.HasListing);
        Assert.False(woolworths.HasPrice);
        Assert.Null(woolworths.UnitPrice);
        Assert.Null(woolworths.Ineligibility);
    }

    /// <summary>
    /// A listing nobody has checked yet is its own answer: the supplier lists the product and no
    /// price has ever been observed. It is not a missing supplier, and it is never a price of zero.
    /// </summary>
    [Fact]
    public async Task A_listing_with_no_observation_is_published_as_not_checked()
    {
        var store = Store(
            gstRegistered: false,
            [Coles],
            Matched(7, [Listing(ColesPreset, 24, 22.80m, SupplierPriceBasis.InclusiveOfGst) with
            {
                Price = null,
            }]));

        var product = (await Summary(store).Handle([7], CancellationToken.None)).Products[0];

        var offer = Assert.Single(product.Suppliers);
        Assert.True(offer.HasListing);
        Assert.False(offer.HasPrice);
        Assert.Null(offer.UnitPrice);
        Assert.Null(offer.PackPrice);
        Assert.Null(product.CheapestUnitPrice);
    }

    /// <summary>
    /// A product the business tracks but nothing has matched has no supplier rows at all: there is
    /// no master product whose listings they would be, and a page of suppliers all saying "no match"
    /// would read as an answer about this product that nobody has looked for yet.
    /// </summary>
    [Theory]
    [InlineData(ProductPriceTrackingStatus.Requested)]
    [InlineData(ProductPriceTrackingStatus.NotFound)]
    [InlineData(ProductPriceTrackingStatus.AwaitingProductDetails)]
    public async Task An_unmatched_product_has_a_status_and_no_supplier_rows(
        ProductPriceTrackingStatus status)
    {
        var store = Store(
            gstRegistered: false,
            [JbMetro],
            new TrackedProductListings(7, status, GstClassification.Taxable, []));

        var product = (await Summary(store).Handle([7], CancellationToken.None)).Products[0];

        Assert.Equal(status, product.TrackingStatus);
        Assert.Empty(product.Suppliers);
        Assert.Null(product.CheapestUnitPrice);
    }

    /// <summary>A product the business does not track at all reads as exactly that.</summary>
    [Fact]
    public async Task An_untracked_product_has_no_status_and_no_supplier_rows()
    {
        var store = Store(
            gstRegistered: false,
            [JbMetro],
            new TrackedProductListings(7, null, GstClassification.Taxable, []));

        var product = (await Summary(store).Handle([7], CancellationToken.None)).Products[0];

        Assert.Null(product.TrackingStatus);
        Assert.Empty(product.Suppliers);
    }

    /// <summary>
    /// The criterion: a match the collector made and nobody has checked is published as such, which
    /// is what lets the popup say "Matched automatically" (#941).
    /// </summary>
    [Fact]
    public async Task An_automatic_match_is_published_as_auto_matched()
    {
        var store = Store(
            gstRegistered: false,
            [JbMetro],
            Matched(
                7,
                [Listing(JbMetroPreset, 24, 22.80m, SupplierPriceBasis.InclusiveOfGst)],
                status: ProductPriceTrackingStatus.AutoMatched));

        var product = (await Summary(store).Handle([7], CancellationToken.None)).Products[0];

        Assert.Equal(ProductPriceTrackingStatus.AutoMatched, product.TrackingStatus);
        Assert.Equal(22.80m / 24, product.CheapestUnitPrice);
    }

    /// <summary>
    /// The criterion: an id the store does not answer for - a product of another business, or one
    /// that exists nowhere - is simply absent. Nothing in the answer confirms that it exists
    /// somewhere.
    /// </summary>
    [Fact]
    public async Task An_id_the_store_does_not_answer_for_is_absent()
    {
        var store = Store(
            gstRegistered: false,
            [JbMetro],
            Matched(7, [Listing(JbMetroPreset, 24, 22.80m, SupplierPriceBasis.InclusiveOfGst)]));

        var summary = await Summary(store).Handle([7, 8], CancellationToken.None);

        Assert.Equal([7], summary.Products.Select(product => product.ProductId));
    }

    #endregion

    private static GetCheapestSupplierSummary Summary(FakeStore store) =>
        new(new FakeCurrentBusinessProvider(1), store, new FakeClock(Now));

    private static FakeStore Store(
        bool gstRegistered,
        IReadOnlyList<LinkedSupplier> suppliers,
        params TrackedProductListings[] products) =>
        new() { Facts = new CheapestSupplierFacts(gstRegistered, suppliers, products) };

    private static TrackedProductListings Matched(
        long productId,
        IReadOnlyList<TrackedListingPrice> listings,
        GstClassification gstRule = GstClassification.Taxable,
        ProductPriceTrackingStatus status = ProductPriceTrackingStatus.Matched) =>
        new(productId, status, gstRule, listings);

    /// <summary>
    /// One supplier's listing with a fresh, in-stock, confirmed observation of its pack price: the
    /// ordinary case each test then changes one fact of.
    /// </summary>
    private static TrackedListingPrice Listing(
        int supplierPresetId,
        int? unitsPerPack,
        decimal packPrice,
        SupplierPriceBasis basis) =>
        new(
            supplierPresetId,
            supplierPresetId,
            ListingConfirmed: true,
            unitsPerPack,
            $"https://example.test/{supplierPresetId}",
            Observed,
            new ObservedPackPrice(
                packPrice,
                basis,
                SupplierGstRate: 0.10m,
                IsPromotion: false,
                PromotionEndsAtUtc: null,
                SupplierStockStatus.InStock,
                Observed,
                SupplierPriceFreshnessPolicy.DailyCheckLimitHours));

    private static TrackedListingPrice WithPrice(
        TrackedListingPrice listing,
        Func<ObservedPackPrice, ObservedPackPrice> change) =>
        listing with { Price = change(listing.Price!) };

    private sealed class FakeStore : ICheapestSupplierSummaryStore
    {
        public CheapestSupplierFacts Facts { get; init; } =
            new(false, [], []);

        public IReadOnlyList<long> RequestedIds { get; private set; } = [];

        public bool WasRead { get; private set; }

        public Task<CheapestSupplierFacts> ReadAsync(
            BusinessId businessId,
            IReadOnlyList<long> productIds,
            CancellationToken cancellationToken)
        {
            WasRead = true;
            RequestedIds = productIds;

            return Task.FromResult(new CheapestSupplierFacts(
                Facts.BusinessIsGstRegistered,
                Facts.Suppliers,
                [.. Facts.Products.Where(product => productIds.Contains(product.ProductId))]));
        }
    }
}
