using Inventory.Domain.Exceptions;
using Inventory.Domain.PriceCatalogue;
using Xunit;

namespace Inventory.UnitTests.Domain.PriceCatalogue;

/// <summary>
/// The cheapest-per-unit comparison of issue #925 (parent #817): pack arithmetic, the two GST bases,
/// the eligibility rules and the ties.
///
/// <para>Every case here is a case where getting it wrong would name the wrong supplier cheapest - a
/// carton compared with a single, an inclusive price compared with an exclusive one, a promotion that
/// finished last week, a price nobody has re-read since Thursday - which is why the cases are the
/// combinations rather than the happy path with variations.</para>
/// </summary>
public class CheapestUnitOfferPolicyTests
{
    private static readonly DateTime Now = new(2026, 10, 28, 6, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// A supplier that publishes GST-inclusive prices, in stock, confirmed, observed an hour ago and
    /// checked daily: the ordinary offer every case below varies one fact of.
    /// </summary>
    private static SupplierUnitOffer Offer(
        long listingId,
        decimal packPrice,
        int? unitsPerPack = 24,
        SupplierPriceBasis basis = SupplierPriceBasis.InclusiveOfGst,
        bool confirmed = true,
        decimal? supplierGstRate = null,
        SupplierStockStatus stockStatus = SupplierStockStatus.InStock,
        DateTime? promotionEndsAtUtc = null,
        DateTime? observedAtUtc = null,
        int freshnessLimitHours = SupplierPriceFreshnessPolicy.DailyCheckLimitHours) =>
        new(
            listingId,
            confirmed,
            unitsPerPack,
            packPrice,
            basis,
            supplierGstRate,
            stockStatus,
            promotionEndsAtUtc,
            observedAtUtc ?? Now.AddHours(-1),
            freshnessLimitHours);

    private static CheapestUnitOffers Compare(
        IReadOnlyList<SupplierUnitOffer> offers,
        decimal? productGstRate = CheapestUnitOfferPolicy.StandardGstRate,
        SupplierPriceBasis basis = SupplierPriceBasis.ExclusiveOfGst) =>
        CheapestUnitOfferPolicy.Compare(offers, productGstRate, basis, Now);

    /// <summary>
    /// The pack arithmetic: the unit price is the pack price divided by the pack size, and the two
    /// bases are each other converted by the rate. Nothing is rounded - <c>$24.00 ÷ 24</c> is a dollar
    /// inclusive and <c>$0.909090…</c> exclusive, and the exclusive figure is published at full
    /// precision because the owner's page rounds it and this policy must not.
    /// </summary>
    [Fact]
    public void A_pack_price_divided_by_the_pack_size_is_the_unit_price_on_both_bases()
    {
        var result = Compare([Offer(1, packPrice: 24.00m, unitsPerPack: 24)]);

        var offer = Assert.Single(result.Offers);
        Assert.Equal(1.00m, offer.UnitPriceIncGst);
        Assert.Equal(1.00m / 1.10m, offer.UnitPriceExGst);
        Assert.Equal(CheapestUnitOfferPolicy.StandardGstRate, offer.GstRate);
        Assert.True(offer.IsComparable);
        Assert.Null(offer.Ineligibility);
    }

    /// <summary>
    /// An ex-GST supplier (JB Metro) and an inc-GST one (a supermarket) for the same product are
    /// compared on one basis, and the cheaper pack price is not necessarily the cheaper offer. Here
    /// the supermarket's $26.40 inclusive carton of 24 is $1.00 ex-GST a can, and JB Metro's $22.80
    /// exclusive carton of 24 is $0.95 - so JB Metro wins ex-GST, which is also what it wins on
    /// inclusive.
    /// </summary>
    [Fact]
    public void An_inclusive_supplier_and_an_exclusive_supplier_are_compared_on_one_basis()
    {
        var supermarket = Offer(1, packPrice: 26.40m, basis: SupplierPriceBasis.InclusiveOfGst);
        var jbMetro = Offer(2, packPrice: 22.80m, basis: SupplierPriceBasis.ExclusiveOfGst);

        var exGst = Compare([supermarket, jbMetro], basis: SupplierPriceBasis.ExclusiveOfGst);

        Assert.Equal(0.95m, exGst.CheapestUnitPrice);
        Assert.Equal(2L, Assert.Single(exGst.Cheapest).SupplierListingId);

        // Both figures are published for both suppliers, so a page never derives the other one.
        var supermarketPrice = exGst.Offers.Single(offer => offer.SupplierListingId == 1);
        Assert.Equal(1.10m, supermarketPrice.UnitPriceIncGst);
        Assert.Equal(1.00m, supermarketPrice.UnitPriceExGst);

        var jbMetroPrice = exGst.Offers.Single(offer => offer.SupplierListingId == 2);
        Assert.Equal(0.95m, jbMetroPrice.UnitPriceExGst);
        Assert.Equal(1.045m, jbMetroPrice.UnitPriceIncGst);

        // And the inclusive comparison is the same two numbers on the other basis.
        var incGst = Compare([supermarket, jbMetro], basis: SupplierPriceBasis.InclusiveOfGst);

        Assert.Equal(1.045m, incGst.CheapestUnitPrice);
        Assert.Equal(2L, Assert.Single(incGst.Cheapest).SupplierListingId);
    }

    /// <summary>
    /// The displayed and compared basis follows <c>Business.GstRegistered</c>, which is the one place
    /// that choice is made.
    /// </summary>
    [Theory]
    [InlineData(true, SupplierPriceBasis.ExclusiveOfGst)]
    [InlineData(false, SupplierPriceBasis.InclusiveOfGst)]
    public void A_registered_business_compares_ex_gst_and_an_unregistered_one_inclusive(
        bool registered,
        SupplierPriceBasis expected) =>
        Assert.Equal(expected, CheapestUnitOfferPolicy.BasisFor(registered));

    /// <summary>
    /// A GST-free product is compared with no GST at all, so both bases are the same number. It must
    /// not be treated as taxable - a 10% difference is more than enough to name the wrong supplier.
    /// </summary>
    [Fact]
    public void A_gst_free_product_has_one_price_on_both_bases()
    {
        var result = Compare([Offer(1, packPrice: 12.00m, unitsPerPack: 24)], productGstRate: 0m);

        var offer = Assert.Single(result.Offers);
        Assert.Equal(0.50m, offer.UnitPriceIncGst);
        Assert.Equal(0.50m, offer.UnitPriceExGst);
        Assert.True(offer.IsComparable);
    }

    /// <summary>
    /// The supplier's own per-product rate wins over the product's rule - JB Metro states the GST on
    /// the line it is selling, which is the better evidence about that line.
    /// </summary>
    [Fact]
    public void A_suppliers_own_rate_takes_precedence_over_the_products_rule()
    {
        var result = Compare(
            [Offer(1, packPrice: 24.00m, basis: SupplierPriceBasis.ExclusiveOfGst, supplierGstRate: 0m)],
            productGstRate: CheapestUnitOfferPolicy.StandardGstRate);

        var offer = Assert.Single(result.Offers);
        Assert.Equal(0m, offer.GstRate);
        Assert.Equal(1.00m, offer.UnitPriceExGst);
        Assert.Equal(1.00m, offer.UnitPriceIncGst);
    }

    /// <summary>
    /// Neither the supplier nor the product states a rate: the offer is <strong>shown</strong>, on the
    /// basis it was observed on, and compared with nothing. That is the owner's decision of 27 October
    /// 2026, and the alternative - assuming 10% - would put the price on the wrong side of the tax.
    /// </summary>
    [Fact]
    public void An_offer_whose_gst_rate_nobody_knows_is_shown_and_not_compared()
    {
        var result = Compare(
            [Offer(1, packPrice: 24.00m, basis: SupplierPriceBasis.InclusiveOfGst)],
            productGstRate: null);

        var offer = Assert.Single(result.Offers);
        Assert.Equal(SupplierOfferIneligibility.GstRateUnknown, offer.Ineligibility);
        Assert.Equal(1.00m, offer.UnitPriceIncGst);
        Assert.Null(offer.UnitPriceExGst);
        Assert.Null(offer.GstRate);

        Assert.Empty(result.Cheapest);
        Assert.Null(result.CheapestUnitPrice);
    }

    /// <summary>
    /// The same offer on an ex-GST supplier publishes the exclusive figure and nothing inclusive: the
    /// unknown rate is not filled in from either side.
    /// </summary>
    [Fact]
    public void An_exclusive_offer_with_no_rate_publishes_only_the_exclusive_price()
    {
        var result = Compare(
            [Offer(1, packPrice: 24.00m, basis: SupplierPriceBasis.ExclusiveOfGst)],
            productGstRate: null);

        var offer = Assert.Single(result.Offers);
        Assert.Equal(1.00m, offer.UnitPriceExGst);
        Assert.Null(offer.UnitPriceIncGst);
    }

    /// <summary>
    /// Two suppliers at one price are one answer with two suppliers in it. Both are returned, in
    /// listing order, and neither is preferred - the owner did not state a preference and this policy
    /// must not invent one.
    /// </summary>
    [Fact]
    public void Every_tie_for_cheapest_is_returned()
    {
        var result = Compare(
            [
                Offer(3, packPrice: 24.00m, unitsPerPack: 24),
                Offer(1, packPrice: 12.00m, unitsPerPack: 12),
                Offer(2, packPrice: 36.00m, unitsPerPack: 24),
            ]);

        Assert.Equal([1L, 3L], result.Cheapest.Select(offer => offer.SupplierListingId).ToArray());
        Assert.Equal(1.00m / 1.10m, result.CheapestUnitPrice);
    }

    /// <summary>
    /// A tie survives a difference in scale: <c>$0.50</c> and <c>$0.5000</c> are one price, and a
    /// comparison that treated the trailing zeros as a difference would silently drop a supplier from
    /// the answer.
    /// </summary>
    [Fact]
    public void A_tie_holds_across_differently_scaled_prices()
    {
        var result = Compare(
            [
                Offer(1, packPrice: 12.00m, unitsPerPack: 24),
                Offer(2, packPrice: 12.0000m, unitsPerPack: 24),
            ],
            productGstRate: 0m);

        Assert.Equal([1L, 2L], result.Cheapest.Select(offer => offer.SupplierListingId).ToArray());
    }

    /// <summary>
    /// A listing with no published pack size has no per-unit price at all: both figures are
    /// <see langword="null"/> and the offer is not compared. Unknown stays unknown - 1 would read a
    /// carton's price as a single's.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public void An_offer_with_no_pack_size_has_no_unit_price(int? unitsPerPack)
    {
        var result = Compare([Offer(1, packPrice: 24.00m, unitsPerPack: unitsPerPack)]);

        var offer = Assert.Single(result.Offers);
        Assert.Equal(SupplierOfferIneligibility.PackSizeUnknown, offer.Ineligibility);
        Assert.Null(offer.UnitPriceExGst);
        Assert.Null(offer.UnitPriceIncGst);
        Assert.Empty(result.Cheapest);
    }

    /// <summary>
    /// An unconfirmed listing is not compared, however good its price looks: a proposed match may be
    /// a different size or another product, and pricing from it would present the matcher's guess as
    /// the cheapest supplier.
    /// </summary>
    [Fact]
    public void An_unconfirmed_listing_is_not_compared()
    {
        var result = Compare(
            [
                Offer(1, packPrice: 12.00m, unitsPerPack: 24, confirmed: false),
                Offer(2, packPrice: 24.00m, unitsPerPack: 24),
            ]);

        var unconfirmed = result.Offers.Single(offer => offer.SupplierListingId == 1);
        Assert.Equal(SupplierOfferIneligibility.ListingNotConfirmed, unconfirmed.Ineligibility);

        // Its unit price is still published: the page shows what was observed and why it is not in
        // the comparison.
        Assert.Equal(0.50m, unconfirmed.UnitPriceIncGst);
        Assert.Equal(2L, Assert.Single(result.Cheapest).SupplierListingId);
    }

    /// <summary>
    /// No stock answer withdraws an offer (owner decision, 1 November 2026): suppliers' stock answers
    /// are unreliable, so an out-of-stock price is compared - and can be the cheapest - like any other.
    /// </summary>
    [Theory]
    [InlineData(SupplierStockStatus.InStock)]
    [InlineData(SupplierStockStatus.Unknown)]
    [InlineData(SupplierStockStatus.OutOfStock)]
    public void No_stock_answer_withdraws_an_offer(SupplierStockStatus status)
    {
        var result = Compare([Offer(1, packPrice: 24.00m, stockStatus: status)]);

        Assert.Null(Assert.Single(result.Offers).Ineligibility);
        Assert.Equal(1L, Assert.Single(result.Cheapest).SupplierListingId);
    }

    /// <summary>
    /// A promotion that has finished is not a price. The boundary belongs to the end instant: a
    /// special that runs until Sunday evening is over at Sunday evening, and a promotion with no
    /// stated end never expires on its own.
    /// </summary>
    [Fact]
    public void A_promotion_is_not_compared_once_it_has_ended()
    {
        var ended = Compare([Offer(1, packPrice: 12.00m, promotionEndsAtUtc: Now)]);
        Assert.Equal(
            SupplierOfferIneligibility.PromotionEnded,
            Assert.Single(ended.Offers).Ineligibility);

        var running = Compare([Offer(1, packPrice: 12.00m, promotionEndsAtUtc: Now.AddTicks(1))]);
        Assert.Null(Assert.Single(running.Offers).Ineligibility);

        var openEnded = Compare([Offer(1, packPrice: 12.00m, promotionEndsAtUtc: null)]);
        Assert.Null(Assert.Single(openEnded.Offers).Ineligibility);
    }

    /// <summary>
    /// A daily supplier's price is good for 30 hours, and stale at 30 - the owner's decision of
    /// 27 October 2026, with the six-hour margin that stops a run starting a little later than
    /// yesterday's from marking every price stale.
    /// </summary>
    [Theory]
    [InlineData(29.99, null)]
    [InlineData(30.0, SupplierOfferIneligibility.Stale)]
    [InlineData(31.0, SupplierOfferIneligibility.Stale)]
    public void A_daily_suppliers_price_is_stale_at_thirty_hours(
        double ageHours,
        SupplierOfferIneligibility? expected)
    {
        var result = Compare(
            [
                Offer(
                    1,
                    packPrice: 24.00m,
                    observedAtUtc: Now.AddHours(-ageHours),
                    freshnessLimitHours: SupplierPriceFreshnessPolicy.DailyCheckLimitHours),
            ]);

        Assert.Equal(expected, Assert.Single(result.Offers).Ineligibility);
    }

    /// <summary>
    /// BIG W is checked on Thursday and Sunday, so its longest legitimate gap is 96 hours and its
    /// stored limit is 102. The same 40-hour-old price that is stale for a daily supplier is still
    /// current for it, which is the whole reason the limit is per supplier and stored rather than one
    /// number in this file.
    /// </summary>
    [Theory]
    [InlineData(40.0, null)]
    [InlineData(101.99, null)]
    [InlineData(102.0, SupplierOfferIneligibility.Stale)]
    public void A_twice_weekly_suppliers_price_is_stale_at_a_hundred_and_two_hours(
        double ageHours,
        SupplierOfferIneligibility? expected)
    {
        var result = Compare(
            [
                Offer(
                    1,
                    packPrice: 24.00m,
                    observedAtUtc: Now.AddHours(-ageHours),
                    freshnessLimitHours: SupplierPriceFreshnessPolicy.TwiceWeeklyCheckLimitHours),
            ]);

        Assert.Equal(expected, Assert.Single(result.Offers).Ineligibility);
    }

    /// <summary>
    /// More than one reason can hold at once, and exactly one is reported, in the order issue #925
    /// states them: pack size, then the match, then the rate, then stock, then the promotion, then
    /// staleness. A page shows the first thing that has to change rather than arguing with itself.
    /// </summary>
    [Fact]
    public void One_reason_is_reported_in_the_issues_own_order()
    {
        var everythingWrong = Offer(
            1,
            packPrice: 24.00m,
            unitsPerPack: null,
            confirmed: false,
            stockStatus: SupplierStockStatus.OutOfStock,
            promotionEndsAtUtc: Now.AddDays(-1),
            observedAtUtc: Now.AddDays(-7));

        Assert.Equal(
            SupplierOfferIneligibility.PackSizeUnknown,
            Assert.Single(Compare([everythingWrong], productGstRate: null).Offers).Ineligibility);

        var confirmedAndStale = everythingWrong with
        {
            UnitsPerPack = 24,
            ListingConfirmed = true,
            StockStatus = SupplierStockStatus.OutOfStock,
        };

        Assert.Equal(
            SupplierOfferIneligibility.GstRateUnknown,
            Assert.Single(Compare([confirmedAndStale], productGstRate: null).Offers).Ineligibility);

        // Out of stock is not a reason (1 November 2026), so the next one in order is reported.
        Assert.Equal(
            SupplierOfferIneligibility.PromotionEnded,
            Assert.Single(Compare([confirmedAndStale]).Offers).Ineligibility);
    }

    /// <summary>
    /// No candidate can be compared: there is no cheapest, and the cheapest price is
    /// <see langword="null"/> rather than zero - which is a price, and the cheapest one there is.
    /// </summary>
    [Fact]
    public void A_product_with_no_eligible_offer_has_no_cheapest()
    {
        var result = Compare(
            [
                Offer(1, packPrice: 24.00m, unitsPerPack: null),
                Offer(2, packPrice: 12.00m, confirmed: false),
                Offer(3, packPrice: 6.00m, promotionEndsAtUtc: Now.AddDays(-1)),
            ]);

        Assert.Empty(result.Cheapest);
        Assert.Null(result.CheapestUnitPrice);

        // And every one of them is still answered, with its own reason.
        Assert.Equal(3, result.Offers.Count);
        Assert.All(result.Offers, offer => Assert.NotNull(offer.Ineligibility));
    }

    /// <summary>No candidates at all is an empty answer, not an error and not a zero.</summary>
    [Fact]
    public void No_candidates_is_an_empty_answer()
    {
        var result = Compare([]);

        Assert.Empty(result.Offers);
        Assert.Empty(result.Cheapest);
        Assert.Null(result.CheapestUnitPrice);
        Assert.Equal(SupplierPriceBasis.ExclusiveOfGst, result.ComparisonBasis);
    }

    /// <summary>
    /// Comparable offers are published cheapest-first and the rest after them, each order broken by
    /// listing id so one set of candidates has exactly one answer.
    /// </summary>
    [Fact]
    public void Comparable_offers_are_published_cheapest_first_with_the_rest_after_them()
    {
        var result = Compare(
            [
                Offer(1, packPrice: 36.00m),
                Offer(2, packPrice: 24.00m, unitsPerPack: null),
                Offer(3, packPrice: 24.00m),
                Offer(4, packPrice: 12.00m, confirmed: false),
            ]);

        Assert.Equal(
            [3L, 1L, 2L, 4L],
            result.Offers.Select(offer => offer.SupplierListingId).ToArray());
    }

    /// <summary>
    /// A rate that is not a proportion is refused rather than calculated with: a stored or submitted
    /// 10 instead of 0.10 would otherwise publish a unit price eleven times the real one.
    /// </summary>
    [Fact]
    public void A_rate_that_is_not_a_proportion_is_refused()
    {
        Assert.Throws<DomainValidationException>(
            () => Compare([Offer(1, packPrice: 24.00m)], productGstRate: 10m));

        Assert.Throws<DomainValidationException>(
            () => Compare([Offer(1, packPrice: 24.00m, supplierGstRate: -0.1m)]));
    }

    /// <summary>A basis that is not one of the declared two is refused, in either position.</summary>
    [Fact]
    public void A_basis_that_is_not_declared_is_refused()
    {
        Assert.Throws<DomainValidationException>(
            () => CheapestUnitOfferPolicy.Compare(
                [Offer(1, packPrice: 24.00m)],
                CheapestUnitOfferPolicy.StandardGstRate,
                (SupplierPriceBasis)0,
                Now));

        Assert.Throws<DomainValidationException>(
            () => Compare([Offer(1, packPrice: 24.00m, basis: (SupplierPriceBasis)99)]));
    }
}
