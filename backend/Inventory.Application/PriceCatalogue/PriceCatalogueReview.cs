using Inventory.Application.Tenancy;
using Inventory.Application.Time;
using Inventory.Domain.Exceptions;
using Inventory.Domain.PriceCatalogue;
using Inventory.Domain.Tenancy;

namespace Inventory.Application.PriceCatalogue;

/// <summary>
/// The whole Super Admin review queue (issue #928, parent #817): the products awaiting a match, the
/// automatic matches waiting to be checked, and the supplier listings waiting for a decision.
///
/// <para>It takes no filter, page, sort or search parameter, for the reason
/// <c>ListSupplierPresets</c> and <c>ListPlatformBusinesses</c> take none. The one value it supplies
/// is the instant, from the application clock, because whether a held reading is still fresh enough
/// to become a price is an answer about <em>now</em> and the page must not be the thing that decides
/// it.</para>
/// </summary>
public sealed class GetPriceCatalogueReviewQueue
{
    private readonly IPriceCatalogueReviewStore _store;
    private readonly IClock _clock;

    public GetPriceCatalogueReviewQueue(IPriceCatalogueReviewStore store, IClock clock)
    {
        _store = store;
        _clock = clock;
    }

    public Task<PriceCatalogueReviewQueue> Handle(CancellationToken cancellationToken) =>
        _store.ReadQueueAsync(_clock.UtcNow, cancellationToken);
}

/// <summary>
/// Decides one supplier listing (issue #928): confirm it at the identity the reviewer reviewed -
/// correcting the pack quantity, the barcode scope or anything else about it in the same request - or
/// refuse it.
///
/// <para><strong>Only a Super Admin reaches this</strong>, and that is the owner's decision of
/// 27 October 2026 rather than a convenience: a supplier listing is shared by every business, so one
/// wrong pack quantity is one wrong "cheapest supplier" for everybody tracking the product. Claude
/// may propose and has no authority to confirm.</para>
///
/// <para><strong>Two audit events, deliberately.</strong> The decision itself, and - when a price
/// outside the band was accepted because the reviewer approved the exception - that exception, which
/// the issue requires to be audited separately because it overrides a financial safeguard. The second
/// is emitted only when an exception was actually used: an approval that was not needed is not an
/// exception, and an approval on a decision that accepted no price did not override anything.</para>
/// </summary>
public sealed class DecideSupplierListingReview
{
    private readonly IPriceCatalogueReviewStore _store;
    private readonly IAuthenticatedActorAccessor _actorAccessor;
    private readonly IPriceCatalogueReviewAudit _audit;
    private readonly IClock _clock;

    public DecideSupplierListingReview(
        IPriceCatalogueReviewStore store,
        IAuthenticatedActorAccessor actorAccessor,
        IPriceCatalogueReviewAudit audit,
        IClock clock)
    {
        _store = store;
        _actorAccessor = actorAccessor;
        _audit = audit;
        _clock = clock;
    }

    /// <returns>
    /// What the decision did, or <see langword="null"/> when no listing has that id.
    /// </returns>
    /// <exception cref="DomainValidationException">
    /// The submitted identity is not one this catalogue stores, or the outcome is not a declared one.
    /// Nothing is written.
    /// </exception>
    /// <exception cref="DomainConflictException">
    /// The listing is no longer waiting for a decision. Nothing is written.
    /// </exception>
    /// <exception cref="BusinessAccessDeniedException">
    /// The request carries no identifiable actor, so there is nobody to record the decision for.
    /// </exception>
    public async Task<SupplierListingReviewResult?> Handle(
        SupplierListingReviewSubmission submission,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submission);

        // Read before anything else, so nothing is decided for a request whose actor the audit trail
        // could not name. The policy at the boundary has already established that this is the
        // configured platform administrator; this is who they are.
        var actor = CurrentActor();

        // Refused here rather than inside the adapter: an undeclared enum value is a submitted value
        // like any other (a deserialized JSON number binds an arbitrary integer), and the status a
        // decision maps to is the Domain's.
        SupplierListingReviewPolicy.StatusFor(submission.Outcome);

        var decidedAtUtc = _clock.UtcNow;

        var result = await _store
            .DecideListingAsync(submission, decidedAtUtc, cancellationToken)
            .ConfigureAwait(false);

        if (result is null)
        {
            return null;
        }

        _audit.Record(new PriceCatalogueReviewAuditEntry(
            actor.DirectoryTenantId,
            actor.ObjectId,
            result.Outcome == SupplierListingReviewOutcome.Confirmed
                ? PriceCatalogueReviewAction.ListingConfirmed
                : PriceCatalogueReviewAction.ListingRejected,
            result.SupplierListingId,
            MasterProductId: null,
            SuggestionId: null,
            WaitingKey: null,
            result.PriceAccepted,
            result.ConfirmedUnitsPerPack,
            decidedAtUtc));

        if (result.PriceExceptionUsed)
        {
            _audit.Record(new PriceCatalogueReviewAuditEntry(
                actor.DirectoryTenantId,
                actor.ObjectId,
                PriceCatalogueReviewAction.PriceExceptionApproved,
                result.SupplierListingId,
                MasterProductId: null,
                SuggestionId: null,
                WaitingKey: null,
                PriceAccepted: true,
                result.ConfirmedUnitsPerPack,
                decidedAtUtc));
        }

        return result;
    }

    private ActorIdentity CurrentActor() =>
        _actorAccessor.GetCurrentActor().Actor
            ?? throw new BusinessAccessDeniedException(BusinessAccessDenialReason.NotAuthenticated);
}

/// <summary>
/// Decides one waiting product (issue #928): accept the matcher's suggestion, pick another existing
/// master product, create one, refuse a suggestion, or record that nothing in the catalogue is this
/// product.
///
/// <para><strong>A match reaches every business that is waiting under the key and tells the reviewer
/// about none of them.</strong> The key is a folded search name and unit label, so one decision
/// necessarily settles every mapping that carries it - which is what makes one product one search -
/// and the answer carries no business id, name or count.</para>
/// </summary>
public sealed class DecideProductMatchReview
{
    private readonly IPriceCatalogueReviewStore _store;
    private readonly IAuthenticatedActorAccessor _actorAccessor;
    private readonly IPriceCatalogueReviewAudit _audit;
    private readonly IClock _clock;

    public DecideProductMatchReview(
        IPriceCatalogueReviewStore store,
        IAuthenticatedActorAccessor actorAccessor,
        IPriceCatalogueReviewAudit audit,
        IClock clock)
    {
        _store = store;
        _actorAccessor = actorAccessor;
        _audit = audit;
        _clock = clock;
    }

    /// <returns>
    /// What the decision did, or <see langword="null"/> when nothing is waiting under that key.
    /// </returns>
    /// <exception cref="DomainValidationException">
    /// The decision is not a declared one, its own values are incomplete, or the master product named
    /// or described is not one this catalogue stores. Nothing is written.
    /// </exception>
    /// <exception cref="DomainConflictException">
    /// The suggestion named is no longer waiting for a decision, or another master product already
    /// holds the description submitted. Nothing is written.
    /// </exception>
    /// <exception cref="BusinessAccessDeniedException">The request carries no identifiable actor.</exception>
    public async Task<ProductMatchReviewResult?> Handle(
        ProductMatchReviewSubmission submission,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submission);

        var actor = CurrentActor();

        if (!ProductMatchReviewPolicy.IsDeclaredDecision(submission.Decision))
        {
            throw new DomainValidationException(
                "A match decision must be one of "
                    + string.Join(", ", Enum.GetNames<ProductMatchReviewDecision>())
                    + ".");
        }

        var decidedAtUtc = _clock.UtcNow;

        var result = await _store
            .DecideMatchAsync(submission, decidedAtUtc, cancellationToken)
            .ConfigureAwait(false);

        if (result is null)
        {
            return null;
        }

        if (result.MasterProductCreated)
        {
            // Beside the match below, never instead of it: this surface is the only writer of
            // MasterProducts, so "a master product came into existence" is its own fact.
            _audit.Record(Entry(
                actor,
                PriceCatalogueReviewAction.MasterProductCreated,
                result,
                decidedAtUtc));
        }

        _audit.Record(Entry(
            actor,
            result.Decision switch
            {
                ProductMatchReviewDecision.RejectSuggestion =>
                    PriceCatalogueReviewAction.MatchSuggestionRejected,
                ProductMatchReviewDecision.MarkNotFound =>
                    PriceCatalogueReviewAction.WaitingProductNotFound,
                _ => PriceCatalogueReviewAction.ProductMatched,
            },
            result,
            decidedAtUtc));

        return result;
    }

    private static PriceCatalogueReviewAuditEntry Entry(
        ActorIdentity actor,
        PriceCatalogueReviewAction action,
        ProductMatchReviewResult result,
        DateTime occurredAtUtc) =>
        new(
            actor.DirectoryTenantId,
            actor.ObjectId,
            action,
            SupplierListingId: null,
            result.MasterProduct?.Id,
            result.SuggestionId,
            result.WaitingKey,
            PriceAccepted: false,
            ConfirmedUnitsPerPack: null,
            occurredAtUtc);

    private ActorIdentity CurrentActor() =>
        _actorAccessor.GetCurrentActor().Actor
            ?? throw new BusinessAccessDeniedException(BusinessAccessDenialReason.NotAuthenticated);
}

/// <summary>
/// Confirms or undoes one automatic match (issue #928, over #941's work).
///
/// <para><strong>An undo is the reason the automatic matcher is allowed to exist.</strong> #941 may
/// link a waiting key to an existing master product by itself, and this is where a person checks
/// that: confirming settles it, and undoing returns the mappings to awaiting a match, moves the
/// proposed listings back to the key, leaves confirmed listings alone, and records the pair as undone
/// so it is never applied automatically again. Both are audited.</para>
/// </summary>
public sealed class DecideAutomaticProductMatchReview
{
    private readonly IPriceCatalogueReviewStore _store;
    private readonly IAuthenticatedActorAccessor _actorAccessor;
    private readonly IPriceCatalogueReviewAudit _audit;
    private readonly IClock _clock;

    public DecideAutomaticProductMatchReview(
        IPriceCatalogueReviewStore store,
        IAuthenticatedActorAccessor actorAccessor,
        IPriceCatalogueReviewAudit audit,
        IClock clock)
    {
        _store = store;
        _actorAccessor = actorAccessor;
        _audit = audit;
        _clock = clock;
    }

    /// <returns>
    /// What the decision did, or <see langword="null"/> when no suggestion has that id.
    /// </returns>
    /// <exception cref="DomainValidationException">The decision is not a declared one.</exception>
    /// <exception cref="DomainConflictException">
    /// The suggestion is not an automatic match waiting to be checked. Nothing is written.
    /// </exception>
    /// <exception cref="BusinessAccessDeniedException">The request carries no identifiable actor.</exception>
    public async Task<AutomaticMatchReviewResult?> Handle(
        long suggestionId,
        AutomaticMatchReviewDecision decision,
        CancellationToken cancellationToken)
    {
        var actor = CurrentActor();

        if (!ProductMatchReviewPolicy.IsDeclaredDecision(decision))
        {
            throw new DomainValidationException(
                "An automatic match decision must be one of "
                    + string.Join(", ", Enum.GetNames<AutomaticMatchReviewDecision>())
                    + ".");
        }

        var decidedAtUtc = _clock.UtcNow;

        var result = await _store
            .DecideAutomaticMatchAsync(suggestionId, decision, decidedAtUtc, cancellationToken)
            .ConfigureAwait(false);

        if (result is null)
        {
            return null;
        }

        _audit.Record(new PriceCatalogueReviewAuditEntry(
            actor.DirectoryTenantId,
            actor.ObjectId,
            result.Decision == AutomaticMatchReviewDecision.Confirm
                ? PriceCatalogueReviewAction.AutomaticMatchConfirmed
                : PriceCatalogueReviewAction.AutomaticMatchUndone,
            SupplierListingId: null,
            result.MasterProduct.Id,
            result.SuggestionId,
            result.WaitingKey,
            PriceAccepted: false,
            ConfirmedUnitsPerPack: null,
            decidedAtUtc));

        return result;
    }

    private ActorIdentity CurrentActor() =>
        _actorAccessor.GetCurrentActor().Actor
            ?? throw new BusinessAccessDeniedException(BusinessAccessDenialReason.NotAuthenticated);
}
