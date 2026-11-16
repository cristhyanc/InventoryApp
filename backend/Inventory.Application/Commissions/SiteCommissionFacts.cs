using Inventory.Domain.FinancialConfiguration;

namespace Inventory.Application.Commissions;

public sealed record SiteCommissionSaleFact(
    long MachineId,
    DateTime MachineAuthorizationTime,
    decimal SettlementValue,
    string? PaymentMethod,
    string? ProductName);

/// <summary>
/// The write transaction one commission-agreement write runs in (issue #1254). Disposing it without
/// <see cref="CommitAsync"/> rolls back everything written through the connection it shares since it
/// began.
///
/// <para>On SQLite it is a <c>BEGIN IMMEDIATE</c> transaction, so the write lock is taken when the
/// transaction opens rather than at its first write. That is what makes the reads inside it
/// authoritative, and it is why both agreement writes take it: the overlap check an added agreement
/// is refused by and the end date a narrowed agreement is compared against have to be read from the
/// same state the write lands on. A deferred transaction would let two callers both pass the overlap
/// check and both insert, or let an agreement be ended between another request's check and its
/// insert. It is the same shape and the same reason as
/// <c>IInventoryCountAdjustmentStore.BeginWriteTransactionAsync</c> (issue #1241).</para>
/// </summary>
public interface ISiteCommissionWriteTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);
}

public interface ISiteCommissionStore
{
    /// <summary>
    /// Opens the write transaction an agreement write runs in (issue #1254). The reads that decide
    /// the write - the overlap check, and the agreement an end date is measured against - must
    /// happen inside it.
    /// </summary>
    Task<ISiteCommissionWriteTransaction> BeginWriteTransactionAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<SiteCommissionSaleFact>> GetCompletedSalesAsync(
        IReadOnlyCollection<long> machineIds,
        DateTime from,
        DateTime endExclusive,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CommissionAgreement>> GetAgreementsAsync(
        long? siteId,
        CancellationToken cancellationToken);

    Task<bool> HasOverlappingAgreementAsync(
        long siteId,
        DateTime effectiveFrom,
        DateTime? effectiveTo,
        CancellationToken cancellationToken);

    /// <summary>
    /// The caller's own agreement with this key, or <see langword="null"/> when their business holds
    /// none - so another business's key and a key that exists nowhere are the same answer
    /// (issue #1254). It is the authoritative read an end date is decided from, and runs inside the
    /// write transaction above.
    /// </summary>
    Task<CommissionAgreement?> FindAgreementAsync(
        int agreementId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Writes the agreement's new end date and stamps <c>UpdatedAt</c> with
    /// <paramref name="occurredAtUtc"/> (issue #1254). It saves inside the caller's transaction and
    /// deliberately does <b>not</b> commit: the use case opened that transaction before it read the
    /// agreement this date was decided against, and owns the commit.
    ///
    /// <para>It writes the end date and nothing else. A rate, a start date, a frequency, a basis and
    /// a due-days value are not editable - correcting one would rewrite commission already reported
    /// - and there is no parameter here that could carry one.</para>
    /// </summary>
    Task<CommissionAgreement> SetAgreementEndDateAsync(
        int agreementId,
        DateTime endDate,
        DateTime occurredAtUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// Inserts the agreement and saves inside the caller's transaction, without committing it
    /// (issue #1254 moved the commit to the use case, which opened the transaction before the
    /// overlap check this insert is allowed by).
    /// </summary>
    Task<CommissionAgreement> AddAgreementAsync(
        CommissionAgreement agreement,
        CancellationToken cancellationToken);
}
