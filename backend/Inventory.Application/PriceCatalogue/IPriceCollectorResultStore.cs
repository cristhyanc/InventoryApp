namespace Inventory.Application.PriceCatalogue;

/// <summary>
/// The narrow write port the collector's results are accepted through (issue #937, parent #817):
/// one batch of rows, one search's answer, and one run's completion.
///
/// <para><strong>Six platform tables and nothing else.</strong> <c>SupplierListings</c>,
/// <c>SupplierPriceObservations</c>, <c>SupplierListingPendingObservations</c>,
/// <c>PriceCollectorResultRows</c>, <c>PriceCollectorRuns</c> and <c>PriceCollectorWorkItems</c>,
/// plus matching evidence on <c>ProductMatchSuggestions</c> - all of them about no business. This
/// port takes no business, no predicate, no column list, no page and no statement, and a request
/// that reaches it carries a <em>denied</em> <c>BusinessScope</c>, so
/// <c>BusinessOwnershipEnforcer</c> refuses every tenant-owned write exactly as it does for the
/// platform-admin surfaces. Nothing tenant-owned is readable or writable through it, and
/// <c>SupplierPriceObservationBoundaryTests</c> keeps every purchase, cost, inventory, GST and
/// reporting area out of reach of all of it.</para>
///
/// <para><strong>Each method is one transaction, and that is the design rather than an
/// optimisation.</strong> Deciding a row means reading the run, the work item, the listing and the
/// listing's last accepted price, and then writing a listing, an observation and the row's own
/// outcome; doing the read and the write in separate transactions would let two submissions of one
/// key both pass the "has this been submitted?" check. The authoritative read, the comparison and
/// the mutation are one operation here, and the idempotency key is a unique index underneath it so
/// a race loses rather than duplicates.</para>
///
/// <para><strong>Every rule is the Domain's.</strong> <c>PriceCollectorResultPolicy</c> decides what
/// becomes of a row, <c>SupplierListingPolicy</c> and <c>SupplierPriceObservationPolicy</c> decide
/// what may be stored, <c>PriceCollectorWorkItemPolicy</c> decides what a search may be answered
/// with, and <c>PriceCollectorRunPolicy</c> decides how a run ended. The adapter behind this port
/// reads, writes and sequences; it does not judge.</para>
/// </summary>
public interface IPriceCollectorResultStore
{
    /// <summary>
    /// Decides and stores one batch of result rows, and answers what became of each.
    ///
    /// <para>Answers <see langword="null"/> when no run has that id, which the endpoint reports as
    /// <c>404</c>: a run nothing issued is not a run whose rows could be refused one by one.</para>
    /// </summary>
    /// <exception cref="Domain.Exceptions.DomainConflictException">
    /// The run has finished or has expired. Nothing is stored - a closed run accepts no results,
    /// and touching an overdue one expires it first.
    /// </exception>
    Task<PriceCollectorResultReceipt?> SubmitAsync(
        PriceCollectorResultBatch batch,
        DateTime asOfUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// Records one search's outcome, having checked the collector's own total against the rows that
    /// actually arrived, and - for a complete or no-match search only - records the known listings
    /// it did not find.
    ///
    /// <para>Answers <see langword="null"/> when that run has no such work item.</para>
    /// </summary>
    /// <exception cref="Domain.Exceptions.DomainConflictException">
    /// The run has finished or expired; the declared total does not match the rows received, which
    /// leaves the search unanswered; or the search was already answered differently.
    /// </exception>
    Task<PriceCollectorSearchAnswerReceipt?> AnswerSearchAsync(
        PriceCollectorSearchAnswer answer,
        DateTime asOfUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// Closes one run: every search still unanswered becomes <c>NotSearched</c>, the per-outcome
    /// counts are written, and the status is derived from them.
    ///
    /// <para>Answers <see langword="null"/> when no run has that id.</para>
    /// </summary>
    /// <exception cref="Domain.Exceptions.DomainConflictException">
    /// The run had already finished - which includes having been expired by its own lifetime or by
    /// a later run.
    /// </exception>
    Task<PriceCollectorRunCompletion?> CompleteRunAsync(
        long runId,
        DateTime asOfUtc,
        CancellationToken cancellationToken);
}
