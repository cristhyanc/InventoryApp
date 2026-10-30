using Inventory.Domain.Exceptions;

namespace Inventory.Domain.PriceCatalogue;

/// <summary>
/// What a Super Admin decided about one supplier listing (issue #928, parent #817): that the listing
/// really is the product it hangs from, or that it is not.
///
/// <para>Two values and no third. "Leave it for later" is not a decision - it is the listing staying
/// where it is - and a decision that could be recorded without being one would be a listing nobody
/// can tell the state of. Numbered from 1, like every other persisted enum in this application,
/// although this one is reported rather than stored: what the database keeps is the listing's own
/// <see cref="SupplierListingMatchStatus"/>.</para>
/// </summary>
public enum SupplierListingReviewOutcome
{
    /// <summary>
    /// Confirmed: this listing is this product, at the pack quantity and barcode scope the reviewer
    /// confirmed. The listing becomes <see cref="SupplierListingMatchStatus.Confirmed"/>, which is
    /// the only status whose readings may become prices (issue #937).
    /// </summary>
    Confirmed = 1,

    /// <summary>
    /// Refused: this listing is not this product. The listing becomes
    /// <see cref="SupplierListingMatchStatus.Rejected"/> and is kept rather than deleted, because a
    /// search finds the same shelf again every run and a refusal has to outlive the row it refused.
    /// </summary>
    Rejected = 2,
}

/// <summary>
/// One reading a listing is holding pending (issue #937's <c>SupplierListingPendingObservations</c>),
/// as the review of #928 judges it: what was read, when, and what it says the pack is.
///
/// <para>It is a parameter object rather than four arguments because the three travel together: a
/// price with no instant cannot be aged and an instant with no identity cannot be compared with what
/// a reviewer confirmed.</para>
/// </summary>
/// <param name="Identity">The identity the collector read, on <see cref="SupplierListingIdentity"/>'s terms.</param>
/// <param name="PackPrice">What the whole pack cost, as read.</param>
/// <param name="ObservedAtUtc">When it was read, which is what freshness is measured from.</param>
public sealed record ReviewedPendingReading(
    SupplierListingIdentity Identity,
    decimal PackPrice,
    DateTime ObservedAtUtc);

/// <summary>
/// What is to be done with the reading a listing holds pending when the listing is confirmed (issue
/// #928): whether it becomes the listing's accepted price, whether it did so only because a price
/// exception was approved, and why not when it did not.
/// </summary>
/// <param name="AcceptsPrice">Whether the reading becomes an accepted observation and the listing's current price.</param>
/// <param name="UsesPriceException">
/// Whether it was accepted <em>despite</em> the band, which is the one thing audited separately from
/// the listing decision. False when the price was inside the band, because then no exception was
/// made - an approval that was not needed is not an exception.
/// </param>
/// <param name="Reason">
/// Why the reading was not accepted, and <see langword="null"/> when it was. Always present for a
/// refusal, because "confirmed with no current price" is a state the reviewer has to be able to
/// account for.
/// </param>
public sealed record ReviewedPendingPriceDecision(
    bool AcceptsPrice,
    bool UsesPriceException,
    string? Reason);

/// <summary>
/// The rules of the Super Admin's review of one supplier listing (issue #928, parent #817): which
/// listings may be decided, what confirming one does to the reading it is holding, and when a price
/// outside the band may be accepted anyway.
///
/// <para><strong>Only a person confirms a listing, and this policy is where that is written.</strong>
/// The owner's decision of 27 October 2026 is that Claude may propose and has no authority to confirm
/// a listing or a pack size, because the data is shared by every business: one wrong pack quantity is
/// one wrong "cheapest supplier" for everybody tracking the product. So the collector's own results
/// endpoint (#937) can only ever hold a reading, and what turns a held reading into a price is a
/// decision made here.</para>
///
/// <para><strong>Approval does not re-price a listing on trust either.</strong> Confirming a listing
/// accepts the reading it holds only when that reading is still fresh enough to be purchasing
/// guidance (<see cref="SupplierPriceFreshnessPolicy"/>, per supplier), still describes the pack the
/// reviewer confirmed, and is inside the band around the listing's last accepted price
/// (<see cref="PriceCollectorResultPolicy"/>) - or outside it with the reviewer's own explicit price
/// exception. Otherwise the listing is confirmed with <strong>no current price</strong> and is priced
/// on the next run, which is the issue's own wording and the same restraint the report availability
/// model shows: a figure nobody can stand behind is reported as absent rather than published.</para>
///
/// <para><strong>The pack quantity is the reviewer's answer and the rest of the identity is the
/// shelf's.</strong> A BIG W row publishes no pack count field - the count is read out of the name
/// ("Coca Cola 375Ml Coke 36Pk 36 Pack"), confirmed by a person, and then stored on the listing - so
/// the pack quantity is exactly the field this review exists to settle, and a reading that disagrees
/// with the reviewer about it does not refuse the price: the confirmed quantity is applied, and the
/// per-unit price <see cref="CheapestUnitOfferPolicy"/> derives follows from it. Every other identity
/// field - the variant, the individual size and its unit, the barcode and its scope - must agree,
/// because a reading of a different pack is not a price for this one. See
/// <see cref="ReadingDescribesConfirmedPack"/>.</para>
///
/// <para><strong>It stores nothing and reads nothing.</strong> Every method is a pure function of the
/// values it is handed, exactly as <see cref="PriceCollectorResultPolicy"/>'s are, so the rules can be
/// stated over a table of cases rather than through an endpoint.</para>
/// </summary>
public static class SupplierListingReviewPolicy
{
    /// <summary>
    /// What a reviewer who tried to decide a listing that is not awaiting a decision is told - one
    /// already confirmed, or one already refused.
    ///
    /// <para>It is a refusal rather than a repeated write because the listing a reviewer was looking
    /// at has moved since: a collector run may have flagged it, or another Super Admin may have
    /// decided it, and applying a decision made against the older state is how a confirmation
    /// silently re-confirms an identity nobody looked at.</para>
    /// </summary>
    public const string NotAwaitingReviewMessage =
        "This supplier listing is not waiting for a decision. Re-read the review list: it has been "
            + "confirmed or refused since.";

    /// <summary>Why a confirmation accepted no price: the listing was holding no reading at all.</summary>
    public const string NoPendingReadingReason =
        "the listing holds no pending reading, so it is confirmed with no current price";

    /// <summary>
    /// Why a confirmation accepted no price: the reading is older than this supplier's own freshness
    /// limit, so it is no longer purchasing guidance.
    /// </summary>
    public const string StaleReadingReason =
        "the pending reading is older than this supplier's price freshness limit";

    /// <summary>
    /// Why a confirmation accepted no price: the reading describes a different pack from the one that
    /// was confirmed - a different variant, individual size or barcode.
    /// </summary>
    public const string ReadingDescribesAnotherPackReason =
        "the pending reading's variant, individual size or barcode is not the one confirmed";

    /// <summary>
    /// Why a confirmation accepted no price: the reading is outside the band around the listing's
    /// last accepted price and no price exception was approved.
    /// </summary>
    public const string PriceExceptionNotApprovedReason =
        "the pending price is more than 50% below or 100% above the last accepted price and no price "
            + "exception was approved";

    /// <summary>
    /// Whether a listing in <paramref name="status"/> is one a reviewer may decide: the collector's
    /// own proposal, and a listing a run flagged.
    ///
    /// <para>A <see cref="SupplierListingMatchStatus.Confirmed"/> listing is settled and a
    /// <see cref="SupplierListingMatchStatus.Rejected"/> one is refused, and neither is shown in the
    /// review list - "a rejected listing is not shown again unless its identity changes", which is
    /// #937's own transition back to <see cref="SupplierListingMatchStatus.Proposed"/> rather than
    /// anything this surface does.</para>
    /// </summary>
    public static bool IsAwaitingReview(SupplierListingMatchStatus status) =>
        status is SupplierListingMatchStatus.Proposed or SupplierListingMatchStatus.NeedsReview;

    /// <summary>Refuses a decision about a listing that is not awaiting one.</summary>
    /// <exception cref="DomainConflictException">
    /// The listing is already confirmed or already refused. Nothing is written.
    /// </exception>
    public static void RequireAwaitingReview(SupplierListingMatchStatus status)
    {
        if (!IsAwaitingReview(status))
        {
            throw new DomainConflictException(NotAwaitingReviewMessage);
        }
    }

    /// <summary>
    /// The match status a decided listing takes: confirmed, or refused.
    /// </summary>
    /// <exception cref="DomainValidationException">The outcome is not a declared one.</exception>
    public static SupplierListingMatchStatus StatusFor(SupplierListingReviewOutcome outcome) => outcome switch
    {
        SupplierListingReviewOutcome.Confirmed => SupplierListingMatchStatus.Confirmed,
        SupplierListingReviewOutcome.Rejected => SupplierListingMatchStatus.Rejected,
        _ => throw new DomainValidationException(
            "A supplier listing review decision must be one of "
                + string.Join(", ", Enum.GetNames<SupplierListingReviewOutcome>())
                + "."),
    };

    /// <summary>
    /// Whether <paramref name="read"/> describes the pack <paramref name="confirmed"/> describes, for
    /// the purpose of accepting the reading's price.
    ///
    /// <para><strong>The pack quantity is deliberately excluded, and that is the whole of the
    /// difference from <see cref="PriceCollectorResultPolicy.HasIdentityChanged"/>.</strong> That rule
    /// compares a reading with what somebody already confirmed, where a changed pack count means the
    /// shelf moved under a settled decision. Here the reviewer <em>is</em> deciding the pack count,
    /// with the reading in front of them: a supplier that publishes no pack count field has it read
    /// out of the listing's name, and refusing the price because the reviewer corrected what was read
    /// would make the correction useless - the listing would be confirmed with no price and the next
    /// run would flag it for the same disagreement. So the confirmed quantity wins and is what the
    /// per-unit price is derived from; every other field still has to agree, because a reading of a
    /// different variant, size or barcode is a price for something else.</para>
    /// </summary>
    public static bool ReadingDescribesConfirmedPack(
        SupplierListingIdentity confirmed,
        SupplierListingIdentity read)
    {
        ArgumentNullException.ThrowIfNull(confirmed);
        ArgumentNullException.ThrowIfNull(read);

        return !PriceCollectorResultPolicy.HasIdentityChanged(
            confirmed with { UnitsPerPack = read.UnitsPerPack },
            read);
    }

    /// <summary>
    /// Whether <paramref name="packPrice"/> may only be accepted with an explicit price exception:
    /// the listing has a last accepted price and this reading is outside the band around it.
    ///
    /// <para>A listing with no accepted price has no band at all, which is #937's own rule and not a
    /// second one stated here.</para>
    /// </summary>
    public static bool NeedsPriceException(decimal? lastAcceptedPackPrice, decimal packPrice) =>
        lastAcceptedPackPrice is { } last
            && !PriceCollectorResultPolicy.IsWithinPriceBand(last, packPrice);

    /// <summary>
    /// What confirming a listing does with the reading it holds pending.
    ///
    /// <para><strong>The order is the rule.</strong> A reading that is not there cannot be judged; a
    /// reading too old to be guidance is not made usable by describing the right pack; a reading of
    /// another pack is not a price for this one whatever the band says; and only then does the band
    /// decide, which is the one condition a reviewer may deliberately override. Each refusal carries
    /// its own sentence, because "confirmed with no current price" is a state the reviewer has to be
    /// able to account for.</para>
    /// </summary>
    /// <param name="confirmed">The identity the reviewer confirmed.</param>
    /// <param name="pending">The reading the listing holds, or <see langword="null"/> when it holds none.</param>
    /// <param name="lastAcceptedPackPrice">
    /// The listing's last accepted pack price, or <see langword="null"/> when it has never had one.
    /// </param>
    /// <param name="freshnessLimitHours">
    /// The supplier master record's own <c>PriceCheckFreshnessHours</c> (issue #925) - never a limit
    /// decided from a supplier's name.
    /// </param>
    /// <param name="asOfUtc">Now, from the application clock.</param>
    /// <param name="priceExceptionApproved">
    /// Whether the reviewer explicitly approved a price outside the band. It is read only when the
    /// band is what would otherwise refuse the reading: an approval cannot make a stale reading fresh
    /// or make a reading of another pack this pack's price.
    /// </param>
    public static ReviewedPendingPriceDecision DecidePendingPrice(
        SupplierListingIdentity confirmed,
        ReviewedPendingReading? pending,
        decimal? lastAcceptedPackPrice,
        int freshnessLimitHours,
        DateTime asOfUtc,
        bool priceExceptionApproved)
    {
        ArgumentNullException.ThrowIfNull(confirmed);

        if (pending is null)
        {
            return Refused(NoPendingReadingReason);
        }

        if (!SupplierPriceFreshnessPolicy.IsFresh(
            pending.ObservedAtUtc,
            asOfUtc,
            freshnessLimitHours))
        {
            return Refused(StaleReadingReason);
        }

        if (!ReadingDescribesConfirmedPack(confirmed, pending.Identity))
        {
            return Refused(ReadingDescribesAnotherPackReason);
        }

        if (!NeedsPriceException(lastAcceptedPackPrice, pending.PackPrice))
        {
            return new ReviewedPendingPriceDecision(
                AcceptsPrice: true,
                UsesPriceException: false,
                Reason: null);
        }

        return priceExceptionApproved
            ? new ReviewedPendingPriceDecision(
                AcceptsPrice: true,
                UsesPriceException: true,
                Reason: null)
            : Refused(PriceExceptionNotApprovedReason);
    }

    /// <summary>
    /// Whether one listing's barcode is the barcode a <em>confirmed</em> listing already carries, in
    /// the same scope - the "strong match" the review list shows first (issue #928).
    ///
    /// <para><strong>It is evidence and never a decision.</strong> A barcode is the one fact two
    /// suppliers can publish about one physical pack - the issue's own examples are a JB Metro inner
    /// barcode that is a Woolworths single's and a JB Metro outer barcode that is a Woolworths
    /// 24-pack's - so a listing whose code matches a confirmed one is almost certainly the same
    /// product. Almost: it still needs confirmation, because what the match proves is that the two
    /// rows describe the same barcode, not that the confirmed row was right.</para>
    ///
    /// <para><strong>The barcode is compared exactly and the scope must agree.</strong> Exactly
    /// because <c>0012345678905</c> and <c>12345678905</c> are different codes and the leading zeros
    /// are the whole difference; the scope because a carton code equal to another listing's unit code
    /// is not evidence that the two are one pack - it is off by the pack size, which is the mistake
    /// <see cref="BarcodeScope"/> exists to prevent.</para>
    /// </summary>
    public static bool IsStrongBarcodeMatch(
        string? barcode,
        BarcodeScope? scope,
        string? confirmedBarcode,
        BarcodeScope? confirmedScope) =>
        barcode is not null
            && scope is not null
            && confirmedBarcode is not null
            && confirmedScope == scope
            && string.Equals(barcode, confirmedBarcode, StringComparison.Ordinal);

    private static ReviewedPendingPriceDecision Refused(string reason) =>
        new(AcceptsPrice: false, UsesPriceException: false, reason);
}
