using Inventory.Domain.Exceptions;
using Inventory.Domain.PriceCatalogue;
using Xunit;

namespace InventoryApi.Tests.Domain.PriceCatalogue;

/// <summary>
/// The rules one submitted collector result row is held to (issue #937, parent #817).
///
/// <para>These are the checks that stand between an untrusted model's output and a stored price, so
/// they are tested as rules rather than through the endpoint: the band in both directions and on both
/// edges, every identity field that sends a listing to review, and the two sanity checks - when a
/// price was read, and whose site it was read from - that reject a row outright.</para>
/// </summary>
public class PriceCollectorResultPolicyTests
{
    private static readonly DateTime RunStarted = new(2026, 10, 28, 6, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = new(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc);

    private static readonly SupplierListingIdentity Confirmed = new(
        "Classic",
        375m,
        MasterProductSizeUnit.Millilitre,
        "0012345678905",
        BarcodeScope.Unit,
        UnitsPerPack: 30);

    #region The batch bound

    [Fact]
    public void Two_hundred_rows_is_the_largest_accepted_batch()
    {
        Assert.Equal(200, PriceCollectorResultPolicy.MaxRowsPerBatch);
        PriceCollectorResultPolicy.RequireAcceptedBatchSize(PriceCollectorResultPolicy.MaxRowsPerBatch);
    }

    [Fact]
    public void A_larger_batch_is_refused()
    {
        var refusal = Assert.Throws<DomainValidationException>(
            () => PriceCollectorResultPolicy.RequireAcceptedBatchSize(
                PriceCollectorResultPolicy.MaxRowsPerBatch + 1));

        Assert.Equal(PriceCollectorResultPolicy.BatchTooLargeMessage, refusal.Message);
    }

    #endregion

    #region The price band

    /// <summary>
    /// The owner's decision of 27 October 2026: no more than 50% below and no more than 100% above
    /// the last accepted price. Both edges are inside the band - "no more than" includes the limit -
    /// and the band is what keeps a mis-read decimal point out of the comparison.
    /// </summary>
    [Theory]
    [InlineData(10.00, 5.00, true)]      // exactly 50% below: the lower edge, inside
    [InlineData(10.00, 20.00, true)]     // exactly 100% above: the upper edge, inside
    [InlineData(10.00, 4.99, false)]     // a cent below the lower edge
    [InlineData(10.00, 20.01, false)]    // a cent above the upper edge
    [InlineData(10.00, 1.00, false)]     // a decimal point lost
    [InlineData(10.00, 100.00, false)]   // a decimal point gained
    [InlineData(10.00, 10.00, true)]
    public void The_band_is_half_the_last_price_to_twice_it(
        decimal lastAccepted,
        decimal observed,
        bool within) =>
        Assert.Equal(
            within,
            PriceCollectorResultPolicy.IsWithinPriceBand(lastAccepted, observed));

    /// <summary>A listing with no accepted price yet has no band, which is the issue's own wording.</summary>
    [Fact]
    public void A_listing_with_no_accepted_price_has_no_band()
    {
        var decision = Decide(
            SupplierListingMatchStatus.Confirmed,
            Confirmed,
            Confirmed,
            packPrice: 999m,
            lastAcceptedPackPrice: null);

        Assert.Equal(PriceCollectorRowOutcome.Accepted, decision.Outcome);
    }

    [Fact]
    public void A_price_outside_the_band_sends_a_confirmed_listing_to_review_and_is_not_accepted()
    {
        var decision = Decide(
            SupplierListingMatchStatus.Confirmed,
            Confirmed,
            Confirmed,
            packPrice: 4.99m,
            lastAcceptedPackPrice: 10m);

        Assert.Equal(PriceCollectorRowOutcome.SentToReview, decision.Outcome);
        Assert.Equal(SupplierListingMatchStatus.NeedsReview, decision.NewMatchStatus);
        Assert.True(decision.StoresPendingObservation);
        Assert.False(decision.RecordsAcceptedObservation);
        Assert.Equal(PriceCollectorResultPolicy.PriceOutsideBandReason, decision.Reason);
    }

    #endregion

    #region Identity and pack changes

    /// <summary>
    /// Every field the issue names, one per case: a confirmed listing whose identity moved is flagged
    /// rather than priced, because the thing that was confirmed is no longer the thing being reported.
    /// </summary>
    [Theory]
    [MemberData(nameof(ChangedIdentities))]
    public void An_identity_change_sends_a_confirmed_listing_to_review(SupplierListingIdentity observed)
    {
        var decision = Decide(
            SupplierListingMatchStatus.Confirmed,
            Confirmed,
            observed,
            packPrice: 10m,
            lastAcceptedPackPrice: 10m);

        Assert.Equal(PriceCollectorRowOutcome.SentToReview, decision.Outcome);
        Assert.Equal(SupplierListingMatchStatus.NeedsReview, decision.NewMatchStatus);
        Assert.True(decision.StoresPendingObservation);
        Assert.False(decision.RecordsAcceptedObservation);
        Assert.Equal(PriceCollectorResultPolicy.IdentityChangedReason, decision.Reason);
    }

    public static TheoryData<SupplierListingIdentity> ChangedIdentities() =>
        new()
        {
            Confirmed with { Variant = "Zero Sugar" },
            Confirmed with { SizeValue = 600m },
            Confirmed with { SizeUnit = MasterProductSizeUnit.Gram },
            Confirmed with { Barcode = "0012345678906" },
            Confirmed with { BarcodeScope = BarcodeScope.Carton },
            Confirmed with { UnitsPerPack = 24 },
        };

    /// <summary>
    /// Case and surrounding space in a supplier's wording are not an identity change: the variant is
    /// the retailer's own text and a re-styled page is not a different product.
    /// </summary>
    [Fact]
    public void A_variant_that_differs_only_in_case_or_spacing_is_not_a_change() =>
        Assert.False(
            PriceCollectorResultPolicy.HasIdentityChanged(
                Confirmed,
                Confirmed with { Variant = "  classic " }));

    /// <summary>
    /// A barcode is compared exactly, because <c>0012345678905</c> and <c>12345678905</c> are
    /// different codes and the leading zeros are the difference.
    /// </summary>
    [Fact]
    public void A_barcode_that_differs_only_by_its_leading_zeros_is_a_change() =>
        Assert.True(
            PriceCollectorResultPolicy.HasIdentityChanged(
                Confirmed,
                Confirmed with { Barcode = "12345678905" }));

    /// <summary>A rejected listing whose identity changed is proposed again rather than priced.</summary>
    [Fact]
    public void A_rejected_listing_whose_identity_changed_becomes_proposed_again()
    {
        var decision = Decide(
            SupplierListingMatchStatus.Rejected,
            Confirmed,
            Confirmed with { UnitsPerPack = 24 },
            packPrice: 10m,
            lastAcceptedPackPrice: 10m);

        Assert.Equal(PriceCollectorRowOutcome.SentToReview, decision.Outcome);
        Assert.Equal(SupplierListingMatchStatus.Proposed, decision.NewMatchStatus);
        Assert.True(decision.StoresPendingObservation);
        Assert.False(decision.RecordsAcceptedObservation);
    }

    /// <summary>And one whose identity is unchanged stays rejected, with the row refused.</summary>
    [Fact]
    public void A_rediscovered_rejected_listing_stays_rejected()
    {
        var decision = Decide(
            SupplierListingMatchStatus.Rejected,
            Confirmed,
            Confirmed,
            packPrice: 10m,
            lastAcceptedPackPrice: 10m);

        Assert.Equal(PriceCollectorRowOutcome.Rejected, decision.Outcome);
        Assert.Null(decision.NewMatchStatus);
        Assert.False(decision.StoresPendingObservation);
        Assert.Equal(PriceCollectorResultPolicy.ListingRejectedReason, decision.Reason);
    }

    #endregion

    #region Unconfirmed listings are never an accepted price

    [Theory]
    [InlineData(SupplierListingMatchStatus.Proposed)]
    [InlineData(SupplierListingMatchStatus.NeedsReview)]
    public void A_proposed_or_flagged_listing_holds_the_row_pending_and_keeps_its_status(
        SupplierListingMatchStatus status)
    {
        var decision = Decide(status, Confirmed, Confirmed, packPrice: 10m, lastAcceptedPackPrice: 10m);

        Assert.Equal(PriceCollectorRowOutcome.Pending, decision.Outcome);
        Assert.Null(decision.NewMatchStatus);
        Assert.True(decision.StoresPendingObservation);
        Assert.False(decision.RecordsAcceptedObservation);
    }

    /// <summary>
    /// A listing nothing has seen before is the collector's own proposal, so its first price is held
    /// pending too: #928 decides whether the match was right before the price is comparable.
    /// </summary>
    [Fact]
    public void A_newly_discovered_listing_is_proposed_and_its_price_is_pending()
    {
        var decision = PriceCollectorResultPolicy.DecideForNewListing();

        Assert.Equal(PriceCollectorRowOutcome.Pending, decision.Outcome);
        Assert.Equal(SupplierListingMatchStatus.Proposed, decision.NewMatchStatus);
        Assert.True(decision.StoresPendingObservation);
        Assert.False(decision.RecordsAcceptedObservation);
    }

    #endregion

    #region When and where a price was read

    [Theory]
    [InlineData(6, 0, true)]    // the run's own start instant
    [InlineData(9, 0, true)]    // now
    [InlineData(5, 59, false)]  // before the run started
    [InlineData(9, 1, false)]   // in the future
    public void An_observation_belongs_to_the_run_window_and_not_to_the_future(
        int hour,
        int minute,
        bool within) =>
        Assert.Equal(
            within,
            PriceCollectorResultPolicy.IsObservedWithinRun(
                new DateTime(2026, 10, 28, hour, minute, 0, DateTimeKind.Utc),
                RunStarted,
                Now));

    [Theory]
    [InlineData("https://www.alpha.example", "https://www.alpha.example/p/1", true)]
    [InlineData("https://alpha.example", "https://www.alpha.example/p/1", true)]
    [InlineData("https://www.alpha.example", "https://shop.alpha.example/p/1", true)]
    [InlineData("https://www.alpha.example", "https://alpha.example.evil.test/p/1", false)]
    [InlineData("https://www.alpha.example", "https://beta.example/p/1", false)]
    [InlineData("https://www.alpha.example", "not a url", false)]
    public void A_source_link_is_read_from_the_suppliers_own_site(
        string? supplierSiteUrl,
        string? sourceUrl,
        bool matches) =>
        Assert.Equal(
            matches,
            PriceCollectorResultPolicy.SourceLinkMatchesSupplier(supplierSiteUrl, sourceUrl));

    /// <summary>
    /// There is nothing to compare when the supplier record holds no site, or the row carries no
    /// link: an absent host is not a mismatched one, and inventing one would refuse every supplier
    /// nobody has recorded a website for.
    /// </summary>
    [Theory]
    [InlineData(null, "https://alpha.example/p/1")]
    [InlineData("  ", "https://alpha.example/p/1")]
    [InlineData("https://alpha.example", null)]
    [InlineData("https://alpha.example", "   ")]
    public void An_absent_site_or_link_is_not_a_mismatch(string? supplierSiteUrl, string? sourceUrl) =>
        Assert.True(PriceCollectorResultPolicy.SourceLinkMatchesSupplier(supplierSiteUrl, sourceUrl));

    #endregion

    private static PriceCollectorRowDecision Decide(
        SupplierListingMatchStatus status,
        SupplierListingIdentity stored,
        SupplierListingIdentity observed,
        decimal packPrice,
        decimal? lastAcceptedPackPrice) =>
        PriceCollectorResultPolicy.DecideForKnownListing(
            status,
            stored,
            observed,
            packPrice,
            lastAcceptedPackPrice);
}
