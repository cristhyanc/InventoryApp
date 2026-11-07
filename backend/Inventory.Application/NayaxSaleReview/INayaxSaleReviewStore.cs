using Inventory.Domain.NayaxSqs;

namespace Inventory.Application.NayaxSaleReview;

/// <summary>
/// One evidence row as the review page shows it (issue #1104's evidence, read for issue #1105).
/// A <see cref="NayaxSaleProviderEvidenceKind.Comparison"/> carries the provider's own values and
/// each field's answer; an <see cref="NayaxSaleProviderEvidenceKind.Absence"/> carries what
/// <c>lastSales</c> returned for the machine - never a claim that the sale is fake.
/// </summary>
public sealed record NayaxSaleReviewEvidence(
    long Id,
    NayaxSaleProviderEvidenceKind Kind,
    DateTime ObservedAtUtc,
    NayaxSqsVerification ResultingState,
    long? ProviderMachineId,
    decimal? ProviderSettlementValue,
    DateTime? ProviderAuthorizationInstantUtc,
    string? ProviderCurrencyCode,
    long? ProviderNayaxProductId,
    string? ProviderProductName,
    NayaxSaleFieldComparison? MachineComparison,
    NayaxSaleFieldComparison? AmountComparison,
    NayaxSaleFieldComparison? InstantComparison,
    NayaxSaleFieldComparison? ProductComparison,
    long? ReadMachineId,
    int? ReturnedItemCount,
    DateTime? ReturnedEarliestInstantUtc,
    DateTime? ReturnedLatestInstantUtc);

/// <summary>One audited Owner decision, or one supersession of it by later evidence.</summary>
public sealed record NayaxSaleReviewHistoryEntry(
    long Id,
    bool Superseded,
    NayaxSaleOwnerDecision? Decision,
    long EvidenceId,
    string? Reason,
    DateTime OccurredAtUtc,
    NayaxSqsVerification PreviousState,
    NayaxSqsVerification ResultingState,
    DateTime? ReconciledPeriodFrom,
    DateTime? ReconciledPeriodTo,
    decimal? ReconciledPeriodDifference);

/// <summary>A decision the page may offer, with the evidence row it would rely on.</summary>
public sealed record NayaxSaleAvailableDecision(NayaxSaleOwnerDecision Decision, long EvidenceId);

/// <summary>
/// One flagged or decided queue-delivered sale on the review page: its stored values, its state, the
/// latest evidence of each kind, its decision history, and the decisions the table allows for it.
/// </summary>
public sealed record NayaxSaleReviewItem(
    long SaleId,
    long TransactionId,
    long MachineId,
    string? MachineName,
    decimal SettlementValue,
    DateTime AuthorizationInstantUtc,
    DateTime? ReceivedViaSqsAtUtc,
    NayaxSqsVerification State,
    bool IsCounted,
    long? NayaxProductId,
    long? ProductId,
    string? ProductName,
    long? LatestEvidenceId,
    NayaxSaleReviewEvidence? LatestComparison,
    NayaxSaleReviewEvidence? LatestAbsence,
    IReadOnlyList<NayaxSaleReviewHistoryEntry> History,
    IReadOnlyList<NayaxSaleAvailableDecision> AvailableDecisions);

/// <summary>
/// The Dashboard's needs-attention figures: how many sales wait for the Owner
/// (<see cref="NayaxSqsVerification.Conflict"/> or <see cref="NayaxSqsVerification.NeedsInvestigation"/>),
/// and how many of those are not being counted (<see cref="NayaxSqsVerification.Conflict"/>).
/// </summary>
public sealed record NayaxSaleReviewSummary(int NeedsReviewCount, int NotCountedCount);

/// <summary>
/// An Owner's decision as the request presented it: the sale, the state and the latest evidence id
/// the page was showing, the evidence row the decision relies on, the decision and its reason.
/// </summary>
public sealed record NayaxSaleDecisionRequest(
    long SaleId,
    NayaxSqsVerification PresentedState,
    long PresentedLatestEvidenceId,
    long EvidenceId,
    NayaxSaleOwnerDecision Decision,
    string? Reason);

/// <summary>Who decided, as the audit records them.</summary>
public sealed record NayaxSaleDecisionActor(string DirectoryTenantId, string ObjectId);

/// <summary>What an applied decision did.</summary>
public sealed record NayaxSaleDecisionApplied(long SaleId, NayaxSqsVerification ResultingState, long AuditId);

/// <summary>
/// The persistence port of the Nayax sales review (issue #1105). Every read and write is an ordinary
/// tenant-scoped statement over the caller's own business: no business id is taken, and there is no
/// <c>IgnoreQueryFilters</c>.
/// </summary>
public interface INayaxSaleReviewStore
{
    /// <summary>The business's flagged and Owner-decided queue sales, newest first.</summary>
    Task<IReadOnlyList<NayaxSaleReviewItem>> ListAsync(CancellationToken cancellationToken);

    /// <summary>The Dashboard's needs-attention figures.</summary>
    Task<NayaxSaleReviewSummary> SummaryAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Applies one decision in a single write-locked transaction: re-read, compare with what the
    /// request presented, resolve the product, apply, audit and record the pending rebuild.
    /// </summary>
    /// <exception cref="Domain.Exceptions.DomainConflictException">
    /// The sale's state or latest evidence is not what the request presented, the named evidence is
    /// not the latest of its kind, or Nayax's product resolves to no product of this business.
    /// Nothing is written.
    /// </exception>
    /// <exception cref="Domain.Exceptions.DomainValidationException">
    /// The decision table refuses the decision, or the reason is missing or too long.
    /// </exception>
    Task<NayaxSaleDecisionApplied?> DecideAsync(
        NayaxSaleDecisionRequest request, NayaxSaleDecisionActor actor, CancellationToken cancellationToken);
}
