using Inventory.Domain.Exceptions;
using Inventory.Domain.PriceCatalogue;
using Xunit;

namespace Inventory.UnitTests.Domain.PriceCatalogue;

/// <summary>
/// The rules of the Super Admin's review of one supplier listing (issue #928, parent #817): which
/// listings may be decided, what confirming one does with the reading it holds, and when a price
/// outside the band may be accepted anyway.
///
/// <para>Stated over a table of cases, because the policy is a pure function of the values it is
/// handed: no database, no clock and no endpoint are involved in any of it.</para>
/// </summary>
public class SupplierListingReviewPolicyTests
{
    private static readonly DateTime Observed = new(2026, 10, 27, 5, 0, 0, DateTimeKind.Utc);

    /// <summary>The identity of the pack the reviewer is looking at, in every case below.</summary>
    private static SupplierListingIdentity Confirmed => new(
        "Classic",
        375m,
        MasterProductSizeUnit.Millilitre,
        "0012345678905",
        BarcodeScope.Unit,
        30);

    [Theory]
    [InlineData(SupplierListingMatchStatus.Proposed, true)]
    [InlineData(SupplierListingMatchStatus.NeedsReview, true)]
    [InlineData(SupplierListingMatchStatus.Confirmed, false)]
    [InlineData(SupplierListingMatchStatus.Rejected, false)]
    public void Only_a_proposal_or_a_flagged_listing_is_awaiting_a_decision(
        SupplierListingMatchStatus status,
        bool awaiting)
    {
        Assert.Equal(awaiting, SupplierListingReviewPolicy.IsAwaitingReview(status));

        if (awaiting)
        {
            SupplierListingReviewPolicy.RequireAwaitingReview(status);
            return;
        }

        var refusal = Assert.Throws<DomainConflictException>(() =>
            SupplierListingReviewPolicy.RequireAwaitingReview(status));
        Assert.Equal(SupplierListingReviewPolicy.NotAwaitingReviewMessage, refusal.Message);
    }

    [Theory]
    [InlineData(SupplierListingReviewOutcome.Confirmed, SupplierListingMatchStatus.Confirmed)]
    [InlineData(SupplierListingReviewOutcome.Rejected, SupplierListingMatchStatus.Rejected)]
    public void A_decision_maps_to_one_match_status(
        SupplierListingReviewOutcome outcome,
        SupplierListingMatchStatus status) =>
        Assert.Equal(status, SupplierListingReviewPolicy.StatusFor(outcome));

    /// <summary>
    /// An enum is a compile-time constraint and not a validation: a deserialized JSON number binds an
    /// arbitrary integer, so an undeclared outcome is refused rather than read as the first declared
    /// one - the rule <c>GstClassifications</c> states for a classification.
    /// </summary>
    [Fact]
    public void An_undeclared_decision_is_refused()
    {
        Assert.Throws<DomainValidationException>(() =>
            SupplierListingReviewPolicy.StatusFor((SupplierListingReviewOutcome)7));
    }

    /// <summary>
    /// A reading of the pack that was confirmed is accepted as its price: fresh, the same product,
    /// and - with no accepted price behind it - no band to be outside.
    /// </summary>
    [Fact]
    public void A_fresh_reading_of_the_confirmed_pack_becomes_the_price()
    {
        var decision = SupplierListingReviewPolicy.DecidePendingPrice(
            Confirmed,
            new ReviewedPendingReading(Confirmed, 42.50m, Observed),
            lastAcceptedPackPrice: null,
            SupplierPriceFreshnessPolicy.DailyCheckLimitHours,
            Observed.AddHours(25),
            priceExceptionApproved: false);

        Assert.True(decision.AcceptsPrice);
        Assert.False(decision.UsesPriceException);
        Assert.Null(decision.Reason);
    }

    [Fact]
    public void A_listing_holding_no_reading_is_confirmed_with_no_price()
    {
        var decision = SupplierListingReviewPolicy.DecidePendingPrice(
            Confirmed,
            pending: null,
            lastAcceptedPackPrice: null,
            SupplierPriceFreshnessPolicy.DailyCheckLimitHours,
            Observed,
            priceExceptionApproved: false);

        Assert.False(decision.AcceptsPrice);
        Assert.Equal(SupplierListingReviewPolicy.NoPendingReadingReason, decision.Reason);
    }

    /// <summary>
    /// The freshness limit is the supplier master record's own, and the boundary belongs to the stale
    /// side deliberately - which is <see cref="SupplierPriceFreshnessPolicy"/>'s rule and not a second
    /// one stated here.
    /// </summary>
    [Theory]
    [InlineData(29, true)]
    [InlineData(30, false)]
    [InlineData(101, false)]
    public void A_reading_is_accepted_only_while_it_is_inside_the_suppliers_own_limit(
        int ageHours,
        bool accepted)
    {
        var decision = SupplierListingReviewPolicy.DecidePendingPrice(
            Confirmed,
            new ReviewedPendingReading(Confirmed, 42.50m, Observed),
            lastAcceptedPackPrice: null,
            SupplierPriceFreshnessPolicy.DailyCheckLimitHours,
            Observed.AddHours(ageHours),
            priceExceptionApproved: false);

        Assert.Equal(accepted, decision.AcceptsPrice);

        if (!accepted)
        {
            Assert.Equal(SupplierListingReviewPolicy.StaleReadingReason, decision.Reason);
        }
    }

    /// <summary>
    /// A supplier checked twice a week has a longer limit, and it is the <em>stored</em> one that
    /// decides: the same reading is fresh for one supplier and stale for another.
    /// </summary>
    [Fact]
    public void The_limit_that_decides_is_the_one_passed_in()
    {
        var fresh = SupplierListingReviewPolicy.DecidePendingPrice(
            Confirmed,
            new ReviewedPendingReading(Confirmed, 42.50m, Observed),
            lastAcceptedPackPrice: null,
            SupplierPriceFreshnessPolicy.TwiceWeeklyCheckLimitHours,
            Observed.AddHours(50),
            priceExceptionApproved: false);

        Assert.True(fresh.AcceptsPrice);
    }

    /// <summary>
    /// Every identity field but the pack quantity has to agree, because a reading of another variant,
    /// size or barcode is a price for something else.
    /// </summary>
    [Theory]
    [InlineData("Zero Sugar", 375, MasterProductSizeUnit.Millilitre, "0012345678905", BarcodeScope.Unit)]
    [InlineData("Classic", 355, MasterProductSizeUnit.Millilitre, "0012345678905", BarcodeScope.Unit)]
    [InlineData("Classic", 375, MasterProductSizeUnit.Gram, "0012345678905", BarcodeScope.Unit)]
    [InlineData("Classic", 375, MasterProductSizeUnit.Millilitre, "12345678905", BarcodeScope.Unit)]
    [InlineData("Classic", 375, MasterProductSizeUnit.Millilitre, "0012345678905", BarcodeScope.Carton)]
    public void A_reading_of_another_pack_is_not_accepted(
        string variant,
        decimal size,
        MasterProductSizeUnit unit,
        string barcode,
        BarcodeScope scope)
    {
        var read = new SupplierListingIdentity(variant, size, unit, barcode, scope, 30);

        Assert.False(SupplierListingReviewPolicy.ReadingDescribesConfirmedPack(Confirmed, read));

        var decision = SupplierListingReviewPolicy.DecidePendingPrice(
            Confirmed,
            new ReviewedPendingReading(read, 42.50m, Observed),
            lastAcceptedPackPrice: null,
            SupplierPriceFreshnessPolicy.DailyCheckLimitHours,
            Observed.AddHours(1),
            priceExceptionApproved: false);

        Assert.False(decision.AcceptsPrice);
        Assert.Equal(
            SupplierListingReviewPolicy.ReadingDescribesAnotherPackReason,
            decision.Reason);
    }

    /// <summary>
    /// The variant is the supplier's own wording, so a restyled page is not a new product: it is
    /// compared trimmed and without regard to case, which is #937's own rule reused rather than
    /// restated.
    /// </summary>
    [Theory]
    [InlineData("  classic  ")]
    [InlineData("CLASSIC")]
    public void A_variant_differing_only_in_case_or_spacing_is_the_same_pack(string variant)
    {
        var read = new SupplierListingIdentity(
            variant,
            375m,
            MasterProductSizeUnit.Millilitre,
            "0012345678905",
            BarcodeScope.Unit,
            30);

        Assert.True(SupplierListingReviewPolicy.ReadingDescribesConfirmedPack(Confirmed, read));
    }

    /// <summary>
    /// <strong>The pack quantity is the reviewer's own answer</strong>, and this is the case that says
    /// so: a supplier that publishes no pack count field has it read out of the listing's name, so a
    /// reviewer who corrects what was read must not thereby lose the price. The corrected quantity is
    /// what the per-unit price is then derived from.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(24)]
    [InlineData(36)]
    public void A_reading_that_disagrees_about_the_pack_quantity_is_still_this_packs_price(
        int? readQuantity)
    {
        var read = new SupplierListingIdentity(
            "Classic",
            375m,
            MasterProductSizeUnit.Millilitre,
            "0012345678905",
            BarcodeScope.Unit,
            readQuantity);

        Assert.True(SupplierListingReviewPolicy.ReadingDescribesConfirmedPack(Confirmed, read));

        var decision = SupplierListingReviewPolicy.DecidePendingPrice(
            Confirmed,
            new ReviewedPendingReading(read, 42.50m, Observed),
            lastAcceptedPackPrice: null,
            SupplierPriceFreshnessPolicy.DailyCheckLimitHours,
            Observed.AddHours(1),
            priceExceptionApproved: false);

        Assert.True(decision.AcceptsPrice);
    }

    /// <summary>
    /// The other side of that answer, and the rule the two must not be confused: a pack quantity the
    /// reviewer <em>changes</em> withdraws the price the listing was already publishing, because that
    /// price was accepted against the stored quantity and a per-unit comparison divides the listing's
    /// pack price by the listing's own pack count. A $40 24-pack confirmed as a 36-pack is not a
    /// 36-pack's price.
    /// </summary>
    [Theory]
    [InlineData(24)]
    [InlineData(36)]
    [InlineData(null)]
    public void A_changed_pack_quantity_withdraws_the_price_the_listing_was_publishing(
        int? confirmedQuantity)
    {
        var confirmed = Confirmed with { UnitsPerPack = confirmedQuantity };

        Assert.True(SupplierListingReviewPolicy.ConfirmationWithdrawsCurrentPrice(Confirmed, confirmed));
    }

    /// <summary>
    /// Every other identity field withdraws it for the same reason: an accepted price of the Classic
    /// 375 mL unit pack is not a price of the Zero Sugar, of a 355 mL item, of another barcode or of
    /// the carton that barcode might be.
    /// </summary>
    [Theory]
    [InlineData("Zero Sugar", 375, MasterProductSizeUnit.Millilitre, "0012345678905", BarcodeScope.Unit)]
    [InlineData("Classic", 355, MasterProductSizeUnit.Millilitre, "0012345678905", BarcodeScope.Unit)]
    [InlineData("Classic", 375, MasterProductSizeUnit.Gram, "0012345678905", BarcodeScope.Unit)]
    [InlineData("Classic", 375, MasterProductSizeUnit.Millilitre, "12345678905", BarcodeScope.Unit)]
    [InlineData("Classic", 375, MasterProductSizeUnit.Millilitre, "0012345678905", BarcodeScope.Carton)]
    public void A_changed_identity_field_withdraws_the_price_the_listing_was_publishing(
        string variant,
        decimal size,
        MasterProductSizeUnit unit,
        string barcode,
        BarcodeScope scope)
    {
        var confirmed = new SupplierListingIdentity(variant, size, unit, barcode, scope, 30);

        Assert.True(SupplierListingReviewPolicy.ConfirmationWithdrawsCurrentPrice(Confirmed, confirmed));
    }

    /// <summary>
    /// A confirmation that corrects nothing keeps the price: the stored observation is still a reading
    /// of this pack, and a restyled variant - the supplier's own wording, trimmed and compared without
    /// regard to case - is the same pack too.
    /// </summary>
    [Theory]
    [InlineData("Classic")]
    [InlineData("  classic  ")]
    [InlineData("CLASSIC")]
    public void A_compatible_identity_keeps_the_price_the_listing_was_publishing(string variant)
    {
        var confirmed = Confirmed with { Variant = variant };

        Assert.False(SupplierListingReviewPolicy.ConfirmationWithdrawsCurrentPrice(Confirmed, confirmed));
    }

    /// <summary>
    /// The band is #937's: no more than 50% below and no more than 100% above the last accepted
    /// price, with both edges <em>inside</em> it, so a supplier running a half-price special needs no
    /// exception at all. A listing with no accepted price has no band.
    /// </summary>
    [Theory]
    [InlineData(null, 1.00, false)]
    [InlineData(40.00, 20.00, false)]
    [InlineData(40.00, 80.00, false)]
    [InlineData(40.00, 19.99, true)]
    [InlineData(40.00, 80.01, true)]
    public void A_price_needs_an_exception_only_outside_the_band(
        double? lastAccepted,
        double packPrice,
        bool needsException)
    {
        var last = lastAccepted is { } value ? (decimal)value : (decimal?)null;

        Assert.Equal(
            needsException,
            SupplierListingReviewPolicy.NeedsPriceException(last, (decimal)packPrice));

        var decision = SupplierListingReviewPolicy.DecidePendingPrice(
            Confirmed,
            new ReviewedPendingReading(Confirmed, (decimal)packPrice, Observed),
            last,
            SupplierPriceFreshnessPolicy.DailyCheckLimitHours,
            Observed.AddHours(1),
            priceExceptionApproved: false);

        Assert.Equal(!needsException, decision.AcceptsPrice);

        if (needsException)
        {
            Assert.Equal(
                SupplierListingReviewPolicy.PriceExceptionNotApprovedReason,
                decision.Reason);
        }
    }

    /// <summary>
    /// With the exception approved an out-of-band price is accepted, and the decision says it was an
    /// exception - which is what the separate audit event is emitted from.
    /// </summary>
    [Fact]
    public void An_approved_exception_accepts_an_out_of_band_price_and_says_so()
    {
        var decision = SupplierListingReviewPolicy.DecidePendingPrice(
            Confirmed,
            new ReviewedPendingReading(Confirmed, 9.00m, Observed),
            lastAcceptedPackPrice: 40.00m,
            SupplierPriceFreshnessPolicy.DailyCheckLimitHours,
            Observed.AddHours(1),
            priceExceptionApproved: true);

        Assert.True(decision.AcceptsPrice);
        Assert.True(decision.UsesPriceException);
    }

    /// <summary>
    /// An approval that was not needed is not an exception: an in-band price is accepted without one,
    /// and nothing is audited as an override.
    /// </summary>
    [Fact]
    public void An_approval_that_was_not_needed_is_not_an_exception()
    {
        var decision = SupplierListingReviewPolicy.DecidePendingPrice(
            Confirmed,
            new ReviewedPendingReading(Confirmed, 39.00m, Observed),
            lastAcceptedPackPrice: 40.00m,
            SupplierPriceFreshnessPolicy.DailyCheckLimitHours,
            Observed.AddHours(1),
            priceExceptionApproved: true);

        Assert.True(decision.AcceptsPrice);
        Assert.False(decision.UsesPriceException);
    }

    /// <summary>
    /// The exception overrides the band and nothing else: a stale reading and a reading of another
    /// pack are refused whether or not one was approved, and each refusal keeps its own reason.
    /// </summary>
    [Fact]
    public void An_approved_exception_overrides_the_band_and_nothing_else()
    {
        var stale = SupplierListingReviewPolicy.DecidePendingPrice(
            Confirmed,
            new ReviewedPendingReading(Confirmed, 9.00m, Observed),
            lastAcceptedPackPrice: 40.00m,
            SupplierPriceFreshnessPolicy.DailyCheckLimitHours,
            Observed.AddHours(31),
            priceExceptionApproved: true);

        Assert.False(stale.AcceptsPrice);
        Assert.False(stale.UsesPriceException);
        Assert.Equal(SupplierListingReviewPolicy.StaleReadingReason, stale.Reason);

        var otherPack = SupplierListingReviewPolicy.DecidePendingPrice(
            Confirmed,
            new ReviewedPendingReading(
                Confirmed with { Variant = "Zero Sugar" },
                9.00m,
                Observed),
            lastAcceptedPackPrice: 40.00m,
            SupplierPriceFreshnessPolicy.DailyCheckLimitHours,
            Observed.AddHours(1),
            priceExceptionApproved: true);

        Assert.False(otherPack.AcceptsPrice);
        Assert.Equal(
            SupplierListingReviewPolicy.ReadingDescribesAnotherPackReason,
            otherPack.Reason);
    }

    /// <summary>
    /// A barcode a confirmed listing already carries, in the same scope, is a strong match. The
    /// barcode is compared exactly - the leading zeros are the whole difference - and the scope must
    /// agree, because a carton code equal to a unit code is off by the pack size.
    /// </summary>
    [Theory]
    [InlineData("80052760", BarcodeScope.Unit, "80052760", BarcodeScope.Unit, true)]
    [InlineData("80052760", BarcodeScope.Carton, "80052760", BarcodeScope.Carton, true)]
    [InlineData("80052760", BarcodeScope.Unit, "80052760", BarcodeScope.Carton, false)]
    [InlineData("080052760", BarcodeScope.Unit, "80052760", BarcodeScope.Unit, false)]
    [InlineData(null, BarcodeScope.Unit, "80052760", BarcodeScope.Unit, false)]
    [InlineData("80052760", BarcodeScope.Unit, null, BarcodeScope.Unit, false)]
    public void A_strong_barcode_match_is_the_same_code_in_the_same_scope(
        string? barcode,
        BarcodeScope scope,
        string? confirmedBarcode,
        BarcodeScope confirmedScope,
        bool strong) =>
        Assert.Equal(
            strong,
            SupplierListingReviewPolicy.IsStrongBarcodeMatch(
                barcode,
                scope,
                confirmedBarcode,
                confirmedScope));

    /// <summary>A listing with no barcode at all matches nothing, which is an answer rather than a gap.</summary>
    [Fact]
    public void A_listing_with_no_barcode_is_never_a_strong_match() =>
        Assert.False(SupplierListingReviewPolicy.IsStrongBarcodeMatch(
            barcode: null,
            scope: null,
            "80052760",
            BarcodeScope.Unit));
}
