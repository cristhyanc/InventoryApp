using Inventory.Application.Tenancy;
using Inventory.Application.Time;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Providers;
using Inventory.Domain.Tenancy;

namespace Inventory.Application.CatalogueReview;

/// <summary>
/// Applies a started provider catalogue review (issue #738, parent #329 decision C1), and only ever
/// exactly that.
///
/// <para><strong>Everything happens in one transaction.</strong> The stored review is loaded, the
/// local rows its decisions name are re-read authoritatively, every link is checked against those
/// rows, and the links, the provider-owned fields and the decided ones are written. The read, the
/// comparison and the write are one operation, so a product that gained a provider id - or a category
/// that took the one this review names - between the start and the apply refuses the apply rather
/// than being overwritten by a plan nobody approved.</para>
///
/// <para><strong>Atomic.</strong> One refused link refuses the whole apply: nothing is linked, nothing
/// is created, nothing is renamed and no field decision is recorded. A half-applied catalogue review
/// is the one outcome an operator cannot reason about, because the rows it did write are the ones the
/// next review will treat as settled.</para>
///
/// <para><strong>Idempotent.</strong> A review is applied once. A second apply of an applied review
/// writes nothing and reports that it changed nothing, rather than failing or repeating itself; and a
/// link that would record the id its row already carries is not a conflict, which is what makes that
/// answer honest at the row level too.</para>
///
/// <para><strong>Nothing a caller submits is written.</strong> The request carries item keys, three
/// actions and the negotiable field answers. Every provider name and group id comes from the stored
/// review's own items, and every local value from the rows read inside the transaction.</para>
///
/// <para><strong>A pre-#792 review is refused outright.</strong> A review stored before the product
/// selling price was removed still carries the provider prices it meant to negotiate, so applying
/// it would carry out only part of the plan the operator was answering. The adapter refuses it on
/// the authoritative row, inside this transaction and before anything is staged, with
/// <see cref="StaleSellingPriceReviewMessage"/>; a decision that still names the retired price
/// field is refused here, with <see cref="RetiredSellingPriceDecisionMessage"/>. Both leave the
/// catalogue exactly as it was.</para>
///
/// <para><strong>It touches no sale and recosts nothing.</strong> Linking a provider product id to a
/// local product changes no stored sale's <c>ProductId</c> and replays no costing: sales already
/// attributed to a product keep that product and its historical cost exactly, and the sales a link
/// makes <em>resolvable</em> are left unresolved for the separate, explicit, audited
/// <see cref="ResolveLinkedProviderSales"/> operation (AGENTS.md § Inventory and historical costing
/// invariants).</para>
/// </summary>
public sealed class ApplyCatalogueReview
{
    /// <summary>The caller-safe refusal when the named review is not one this business holds.</summary>
    public const string UnknownReviewMessage =
        "This catalogue review does not exist. Start the review again.";

    /// <summary>
    /// The refusal for a review stored before issue #792 removed the product selling price.
    ///
    /// <para>Such a review was read against a catalogue field that no longer exists, and its entries
    /// still carry the provider prices it was going to negotiate. Applying it would mean carrying
    /// out half a plan - the links and the provider-owned names and categories, but not the price
    /// decisions the operator was answering - so the whole apply is refused before anything is
    /// written, and the operator is asked for a reading of the catalogue as it is now.</para>
    /// </summary>
    public const string StaleSellingPriceReviewMessage =
        "This catalogue review was started before product prices were removed, so it cannot be "
            + "applied. Nothing was changed. Start the review again.";

    /// <summary>
    /// The refusal for a decision about the retired selling-price field. A product carries no price
    /// since issue #792, so there is nothing to keep or take; an older client that still sends one
    /// is refused outright rather than having it silently dropped, because an apply that reported
    /// success would say the operator's price answer had been carried out.
    /// </summary>
    public const string RetiredSellingPriceDecisionMessage =
        "a product's price is no longer part of the catalogue, so it cannot be decided here.";

    private readonly ICatalogueReviewStore _store;
    private readonly IAuthenticatedActorAccessor _actors;
    private readonly IClock _clock;

    public ApplyCatalogueReview(
        ICatalogueReviewStore store, IAuthenticatedActorAccessor actors, IClock clock)
    {
        _store = store;
        _actors = actors;
        _clock = clock;
    }

    public async Task<CatalogueReviewApplied> Handle(
        ApplyCatalogueReviewRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Resolved before anything is read or written: an apply that cannot name who applied it is
        // not auditable, so an unidentifiable caller is refused rather than recorded as blank.
        var actor = _actors.GetCurrentActor();
        var identity = actor.Actor
            ?? throw new BusinessAccessDeniedException(
                actor.DenialReason ?? BusinessAccessDenialReason.UnidentifiableActor);

        await using var transaction = await _store.BeginTransactionAsync(cancellationToken);
        var review = await _store.FindReviewAsync(request.ReviewId, cancellationToken)
            ?? throw new DomainValidationException(UnknownReviewMessage);

        if (review.AppliedAtUtc is { } appliedAlreadyAt)
            return Unchanged(review, appliedAlreadyAt);

        var decisions = Decisions(review, request.Decisions ?? []);
        var applied = await _store.ApplyAsync(
            new CatalogueReviewApplyPlan(review, _clock.UtcNow, identity.ToString(), decisions),
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return applied;
    }

    /// <summary>
    /// The answer for a review that has already been applied: what it did is recorded on the rows it
    /// wrote, and doing it again would not be idempotence but a second apply. Nothing is written, so
    /// the transaction rolls back clean on disposal.
    /// </summary>
    private static CatalogueReviewApplied Unchanged(PersistedCatalogueReview review, DateTime appliedAt) =>
        new(review.ReviewId, AlreadyApplied: true, appliedAt, [], [], [], [], UnresolvedSaleCount: 0);

    /// <summary>
    /// Validates the submitted decisions against the stored review and turns them into the plan.
    /// These are the refusals that are statements about the <em>decision</em> - an item that is not in
    /// this review, a duplicate, a missing or surplus local row, a link of an already-linked entry -
    /// so they are answered before anything local is read. Every refusal is raised, and raised with
    /// the whole list, because an apply that silently dropped the one decision it could not carry out
    /// would report success for a catalogue the operator never approved.
    /// </summary>
    private static IReadOnlyList<CatalogueReviewApplyDecision> Decisions(
        PersistedCatalogueReview review,
        IReadOnlyList<CatalogueReviewDecisionRequest> submitted)
    {
        var itemsByKey = review.Items.ToDictionary(item => item.ItemId);
        var refusals = new List<string>();
        var decisions = new List<CatalogueReviewApplyDecision>();
        var seen = new HashSet<long>();

        foreach (var decision in submitted)
        {
            if (!itemsByKey.TryGetValue(decision.ItemId, out var item))
            {
                refusals.Add($"Entry {decision.ItemId} is not part of this review.");
                continue;
            }

            if (!seen.Add(decision.ItemId))
            {
                refusals.Add($"Entry {decision.ItemId} was decided more than once.");
                continue;
            }

            var alreadyLinked = item.Entry.LinkedProductId.HasValue || item.Entry.LinkedCategoryId.HasValue;
            if (CatalogueReviewPolicy.DecisionRefusal(decision.Action, decision.LocalRowId, alreadyLinked)
                is { } refusal)
            {
                refusals.Add($"Entry {decision.ItemId}: {refusal}");
                continue;
            }

            var fields = decision.Fields ?? [];
            if (fields.Select(field => field.Field).Distinct().Count() != fields.Count)
            {
                refusals.Add($"Entry {decision.ItemId}: a field was decided more than once.");
                continue;
            }

            if (item.Entry.Kind == ProviderCatalogueEntryKind.Category && fields.Count > 0)
            {
                refusals.Add(
                    $"Entry {decision.ItemId}: a category has no negotiable fields - the provider owns "
                        + "its name and nothing else of it is imported.");
                continue;
            }

            if (fields.Any(field => field.Field == NegotiableCatalogueField.UnitPrice))
            {
                refusals.Add($"Entry {decision.ItemId}: {RetiredSellingPriceDecisionMessage}");
                continue;
            }

            decisions.Add(new CatalogueReviewApplyDecision(
                decision.ItemId,
                decision.Action,
                decision.LocalRowId,
                FieldDecision(fields, NegotiableCatalogueField.UnitVolume)));
        }

        if (refusals.Count > 0)
            throw new DomainValidationException(
                "Nothing was applied. " + string.Join(" ", refusals));

        return decisions;
    }

    private static ProviderFieldDecision? FieldDecision(
        IReadOnlyList<CatalogueReviewFieldDecisionRequest> fields, NegotiableCatalogueField field) =>
        fields.FirstOrDefault(submitted => submitted.Field == field)?.Decision;
}
