using Inventory.Domain.Exceptions;
using Inventory.Domain.PriceCatalogue;
using Xunit;

namespace Inventory.UnitTests.Domain.PriceCatalogue;

/// <summary>
/// What may be stored as one observed supplier price (issue #925): the currency, the amounts, the
/// supplier's own GST rate, the promotion and the source.
///
/// <para>Each refusal here is a value that would otherwise be compared as though it meant something
/// else - a zero price that is cheaper than everything, a percentage stored where a proportion
/// belongs, a promotion end date on a price that is not a promotion.</para>
/// </summary>
public class SupplierPriceObservationPolicyTests
{
    [Theory]
    [InlineData("AUD")]
    [InlineData(" aud ")]
    public void The_one_accepted_currency_is_stored_in_its_declared_spelling(string submitted) =>
        Assert.Equal(
            SupplierPriceObservationPolicy.Currency,
            SupplierPriceObservationPolicy.NormaliseCurrency(submitted));

    /// <summary>
    /// A blank currency is refused rather than defaulted to dollars: the collector knows what it
    /// read, and a price whose currency nobody recorded is not evidence that it was in AUD.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("NZD")]
    public void Any_other_currency_is_refused(string? submitted)
    {
        var refusal = Assert.Throws<DomainValidationException>(
            () => SupplierPriceObservationPolicy.NormaliseCurrency(submitted));

        Assert.Equal(SupplierPriceObservationPolicy.CurrencyNotSupportedMessage, refusal.Message);
    }

    /// <summary>
    /// A pack price of zero divides into a unit price of zero, which compares as cheaper than every
    /// real offer, so it is refused rather than stored.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_pack_price_that_is_not_positive_is_refused(decimal packPrice)
    {
        var refusal = Assert.Throws<DomainValidationException>(
            () => SupplierPriceObservationPolicy.RequirePackPrice(packPrice));

        Assert.Equal(SupplierPriceObservationPolicy.PackPriceNotPositiveMessage, refusal.Message);
    }

    [Fact]
    public void A_positive_pack_price_is_stored_exactly_as_observed() =>
        Assert.Equal(26.4000m, SupplierPriceObservationPolicy.RequirePackPrice(26.4000m));

    /// <summary>
    /// "We do not know the normal price" is a real answer and stays <see langword="null"/>; it is
    /// never filled in with the promotional price, which would make a promotion's saving unreadable.
    /// </summary>
    [Fact]
    public void A_regular_price_may_be_unknown() =>
        Assert.Null(SupplierPriceObservationPolicy.NormaliseRegularPrice(null));

    [Fact]
    public void A_regular_price_that_is_not_positive_is_refused() =>
        Assert.Throws<DomainValidationException>(
            () => SupplierPriceObservationPolicy.NormaliseRegularPrice(0m));

    /// <summary>A rate is a proportion: 10% GST is 0.10, and the bounds are both accepted.</summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.1)]
    [InlineData(1.0)]
    public void A_gst_rate_that_is_a_proportion_is_accepted(double rate) =>
        Assert.Equal(
            (decimal)rate,
            SupplierPriceObservationPolicy.NormaliseGstRate((decimal)rate));

    /// <summary>
    /// An unknown rate stays unknown. It is not defaulted to the standard rate and not to zero:
    /// either would be this application inventing a tax basis.
    /// </summary>
    [Fact]
    public void An_unknown_gst_rate_stays_unknown() =>
        Assert.Null(SupplierPriceObservationPolicy.NormaliseGstRate(null));

    /// <summary>
    /// A percentage submitted where a proportion belongs - 10 instead of 0.10 - is refused rather
    /// than stored, because a unit price converted with it is eleven times the real one.
    /// </summary>
    [Theory]
    [InlineData(10.0)]
    [InlineData(-0.1)]
    public void A_gst_rate_that_is_not_a_proportion_is_refused(double rate)
    {
        var refusal = Assert.Throws<DomainValidationException>(
            () => SupplierPriceObservationPolicy.NormaliseGstRate((decimal)rate));

        Assert.Equal(SupplierPriceObservationPolicy.GstRateOutOfRangeMessage, refusal.Message);
    }

    /// <summary>
    /// A promotion with no stated end is the ordinary case and is accepted; an end date on a price
    /// that is not a promotion is refused, because an ineligible "promotion ended" answer would
    /// silently withdraw a real price from the comparison.
    /// </summary>
    [Fact]
    public void A_promotion_end_date_belongs_to_a_promotion()
    {
        var endsAt = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.Null(
            Record.Exception(
                () => SupplierPriceObservationPolicy.RequireConsistentPromotion(true, endsAt)));
        Assert.Null(
            Record.Exception(
                () => SupplierPriceObservationPolicy.RequireConsistentPromotion(true, null)));
        Assert.Null(
            Record.Exception(
                () => SupplierPriceObservationPolicy.RequireConsistentPromotion(false, null)));

        var refusal = Assert.Throws<DomainValidationException>(
            () => SupplierPriceObservationPolicy.RequireConsistentPromotion(false, endsAt));

        Assert.Equal(
            SupplierPriceObservationPolicy.PromotionEndWithoutPromotionMessage,
            refusal.Message);
    }

    /// <summary>"No reference" and "no link" each have one stored spelling, which is null.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_source_is_stored_as_nothing(string? submitted)
    {
        Assert.Null(SupplierPriceObservationPolicy.NormaliseSourceReference(submitted));
        Assert.Null(SupplierPriceObservationPolicy.NormaliseSourceUrl(submitted));
    }

    [Fact]
    public void A_source_reference_and_link_are_stored_trimmed()
    {
        Assert.Equal(
            "run-7:sku-12",
            SupplierPriceObservationPolicy.NormaliseSourceReference("  run-7:sku-12 "));
        Assert.Equal(
            "https://example.test/product/12",
            SupplierPriceObservationPolicy.NormaliseSourceUrl(" https://example.test/product/12 "));
    }

    [Fact]
    public void A_source_reference_or_link_that_is_too_long_is_refused()
    {
        Assert.Throws<DomainValidationException>(
            () => SupplierPriceObservationPolicy.NormaliseSourceReference(
                new string('a', SupplierPriceObservationPolicy.MaxSourceReferenceLength + 1)));

        Assert.Throws<DomainValidationException>(
            () => SupplierPriceObservationPolicy.NormaliseSourceUrl(
                new string('a', SupplierPriceObservationPolicy.MaxSourceUrlLength + 1)));
    }

    /// <summary>
    /// Both vocabularies are validation boundaries and not only compile-time constraints: each
    /// arrives from a JSON body as an arbitrary integer.
    /// </summary>
    [Fact]
    public void A_basis_or_stock_status_outside_its_vocabulary_is_refused()
    {
        Assert.Null(
            Record.Exception(
                () => SupplierPriceObservationPolicy.RequireDeclaredVocabulary(
                    SupplierPriceBasis.InclusiveOfGst,
                    SupplierStockStatus.Unknown)));

        Assert.Throws<DomainValidationException>(
            () => SupplierPriceObservationPolicy.RequireDeclaredVocabulary(
                (SupplierPriceBasis)0,
                SupplierStockStatus.InStock));

        Assert.Throws<DomainValidationException>(
            () => SupplierPriceObservationPolicy.RequireDeclaredVocabulary(
                SupplierPriceBasis.ExclusiveOfGst,
                (SupplierStockStatus)9));
    }

    /// <summary>
    /// Both persisted vocabularies are numbered from 1, so a stored <c>0</c> - which nothing writes -
    /// is never read as the first declared value.
    /// </summary>
    [Fact]
    public void The_persisted_vocabularies_are_numbered_from_one()
    {
        Assert.Equal(1, (int)SupplierPriceBasis.ExclusiveOfGst);
        Assert.Equal(1, (int)SupplierStockStatus.InStock);
        Assert.Equal(1, (int)PriceCollectorRunStatus.Running);
        Assert.Equal(1, (int)SupplierOfferIneligibility.PackSizeUnknown);
    }
}
