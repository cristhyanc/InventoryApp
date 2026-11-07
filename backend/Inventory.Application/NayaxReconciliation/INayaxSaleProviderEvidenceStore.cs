using Inventory.Domain.NayaxSqs;

namespace Inventory.Application.NayaxReconciliation;

/// <summary>
/// One stored sale the reconciliation may compare: a sale this business's queue delivered (issue
/// #1104).
///
/// <para>Only queue-delivered sales are in this model at all. A sale the <c>lastSales</c> sync or an
/// uploaded export imported carries no <c>SqsVerification</c> value, so there is no delivery to
/// verify and nothing here would mean anything about it.</para>
/// </summary>
/// <param name="SaleId">The sale row's own key, which is what an evidence row names.</param>
/// <param name="TransactionId">The Nayax transaction identifier the comparison is matched by.</param>
/// <param name="Verification">The state the row holds right now.</param>
/// <param name="ReceivedViaSqsAtUtc">
/// When the queue reader accepted the message that stored it, which is what the absence rule's
/// receipt grace is measured from.
/// </param>
/// <param name="Values">The fields the comparison reads, as this application holds them.</param>
public sealed record NayaxVerifiableSale(
    long SaleId,
    long TransactionId,
    NayaxSqsVerification Verification,
    DateTime? ReceivedViaSqsAtUtc,
    NayaxStoredSaleValues Values);

/// <summary>
/// One <see cref="NayaxSaleProviderEvidenceKind.Comparison"/> observation, ready to be recorded
/// (issue #1104).
/// </summary>
/// <param name="SaleId">The sale this is evidence about.</param>
/// <param name="RunId">The reconciliation run that observed it.</param>
/// <param name="ObservedAtUtc">When the run observed it.</param>
/// <param name="Provider">
/// The <c>lastSales</c> item's values <strong>exactly as received</strong>. Nothing here is rounded,
/// trimmed or upper-cased: the normalisation belongs to the comparison, and the row has to say what
/// the provider actually sent.
/// </param>
/// <param name="Fields">Each field's own comparison result.</param>
/// <param name="ObservedState">
/// The state this comparison is evidence for, or <see langword="null"/> when it is evidence for none.
/// </param>
public sealed record NayaxSaleComparisonEvidence(
    long SaleId,
    Guid RunId,
    DateTime ObservedAtUtc,
    NayaxProviderSaleValues Provider,
    NayaxSaleFieldComparisons Fields,
    NayaxSqsVerification? ObservedState);

/// <summary>
/// One <see cref="NayaxSaleProviderEvidenceKind.Absence"/> observation, ready to be recorded (issue
/// #1104). It records what was seen and <strong>claims no completeness</strong> about the provider's
/// response - see <see cref="NayaxSaleProviderAbsencePolicy"/>.
/// </summary>
/// <param name="SaleId">The sale this is evidence about.</param>
/// <param name="RunId">The reconciliation run that observed it.</param>
/// <param name="ObservedAtUtc">When the run observed it.</param>
/// <param name="Window">What the response did return for the sale's machine.</param>
/// <param name="SaleAuthorizationInstantUtc">The sale's own stored authorization instant.</param>
/// <param name="SaleReceivedViaSqsAtUtc">When the queue reader accepted its message.</param>
/// <param name="Observation">The two observations and the state they are evidence for.</param>
public sealed record NayaxSaleAbsenceEvidence(
    long SaleId,
    Guid RunId,
    DateTime ObservedAtUtc,
    NayaxProviderMachineWindow Window,
    DateTime? SaleAuthorizationInstantUtc,
    DateTime? SaleReceivedViaSqsAtUtc,
    NayaxSaleAbsenceObservation Observation);

/// <summary>
/// What recording one piece of evidence did (issue #1104).
/// </summary>
/// <param name="Written">
/// Whether a row was appended. A repeated identical observation that changed no state writes
/// nothing: the evidence table is a record of what was learnt, and an hourly run re-reading the same
/// rolling window learns nothing new about a sale it already compared.
/// </param>
/// <param name="EvidenceId">
/// The appended row's own key, so issue #1105's Owner decision can name exactly the evidence it
/// acted on, or <see langword="null"/> when nothing was written.
/// </param>
/// <param name="State">The state the sale holds after this evidence.</param>
/// <param name="StateChanged">Whether that differs from the state it held before.</param>
public sealed record NayaxSaleEvidenceWriteResult(
    bool Written,
    long? EvidenceId,
    NayaxSqsVerification? State,
    bool StateChanged);

/// <summary>
/// The reconciliation's own narrow persistence port (issue #1104): which of this business's
/// queue-delivered sales the provider's response speaks about, and the append-only evidence of what
/// it said.
///
/// <para><strong>Every write is one write-locked transaction that re-reads the sale.</strong> The
/// state a transition is decided from has to be the stored state at the moment it is written, not the
/// state a read a few machines ago saw - and issue #1105's Owner decisions will take the same write
/// lock, so a decision can never interleave between this run's read and its write. That is why
/// recording evidence is a method on a port rather than a state change the use case stages and saves
/// later: the authoritative read, the comparison against it and the mutation are one operation.</para>
///
/// <para><strong>Nothing here takes a business id.</strong> Every method runs in the service scope
/// resolved to the one business being reconciled, so the central tenant query filters find the rows
/// and <c>BusinessOwnershipEnforcer</c> checks the writes - the ordinary mechanism, with nothing
/// added. A business id a method accepted would be a business id a caller could get wrong.</para>
/// </summary>
public interface INayaxSaleProviderEvidenceStore
{
    /// <summary>
    /// This business's queue-delivered sales whose Nayax transaction identifier is one of
    /// <paramref name="transactionIds"/> - the sales the provider's response speaks about directly.
    ///
    /// <para>A transaction id the provider returned that matches no queue-delivered sale answers
    /// nothing, which is correct: it is either a sale only <c>lastSales</c> ever delivered, or one
    /// this run has just created, and neither has a queue delivery to verify.</para>
    /// </summary>
    Task<IReadOnlyList<NayaxVerifiableSale>> FindQueueSalesByTransactionIdsAsync(
        IReadOnlyCollection<long> transactionIds, CancellationToken cancellationToken);

    /// <summary>
    /// This business's queue-delivered sales on <paramref name="machineId"/> whose stored
    /// authorization instant lies <strong>strictly</strong> between
    /// <paramref name="earliestExclusiveUtc"/> and <paramref name="latestExclusiveUtc"/>.
    ///
    /// <para>The range is the response's own: bounding the read by it is what keeps the absence
    /// question answerable at all, because those are the only instants the provider demonstrably
    /// reached. A sale outside the range is not evidence of anything and is never read.</para>
    /// </summary>
    Task<IReadOnlyList<NayaxVerifiableSale>> FindQueueSalesBracketedByAsync(
        long machineId,
        DateTime earliestExclusiveUtc,
        DateTime latestExclusiveUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// Appends one comparison and applies the state transition it allows, in one write-locked
    /// transaction with a re-read of the sale's current state - or writes nothing, when this run
    /// changed no state and the observation is identical to that sale's latest comparison.
    /// </summary>
    Task<NayaxSaleEvidenceWriteResult> RecordComparisonAsync(
        NayaxSaleComparisonEvidence evidence, CancellationToken cancellationToken);

    /// <summary>
    /// The same, for an absence. Only an observation that
    /// <see cref="NayaxSaleAbsenceObservation.RecordsEvidence"/> is ever passed here.
    /// </summary>
    Task<NayaxSaleEvidenceWriteResult> RecordAbsenceAsync(
        NayaxSaleAbsenceEvidence evidence, CancellationToken cancellationToken);
}
