namespace Inventory.Application.PickList;

/// <summary>
/// The write transaction one batch refill runs in (issue #743). Disposing it without
/// <see cref="CommitAsync"/> rolls back everything written through the connection it shares since
/// it began - every movement of the batch, the batch row itself and every product quantity and cost
/// the rebuild touched.
///
/// <para>On SQLite it is a <c>BEGIN IMMEDIATE</c> transaction, so the write lock is taken when the
/// transaction opens rather than at its first write. That is what makes the reads inside it
/// authoritative: the idempotency key claim and the storage-stock check both decide a write, and a
/// deferred transaction would let two callers read the same answer and both act on it. It is the
/// same shape and the same reason as <c>IBusinessMembershipWriteStore.BeginWriteTransactionAsync</c>
/// (issue #522) and <c>INayaxConnectionSaveTransactions</c> (issue #537).</para>
/// </summary>
public interface IPickListRefillWriteTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Narrow persistence port for the batch refill (issue #743, parent #329 child H1), owned by the
/// Application layer.
///
/// <para>It owns no rule. The validation is <c>Inventory.Domain.Stock.PickListRefillPolicy</c>'s,
/// the facts it is decided from are read through the Pick List's existing storage-stock port and the
/// machines port, and the costing of each movement is the established
/// <c>IRecordInventoryMovement</c>/<c>IRebuildProductCost</c> pair's - a refill records exactly the
/// movement the single-product action already records, so its costing and COGS behaviour is
/// unchanged by construction.</para>
/// </summary>
public interface IPickListRefillStore
{
    /// <summary>
    /// Opens the write transaction the whole refill runs in. Every read that decides the write - the
    /// key claim and the storage quantities - must happen inside it.
    /// </summary>
    Task<IPickListRefillWriteTransaction> BeginWriteTransactionAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The refill already recorded under <paramref name="idempotencyKey"/> for the caller's
    /// business, or <see langword="null"/> when that key has recorded nothing. Scoped by the
    /// ordinary tenant query filters, so one business's key never finds another's refill even when
    /// both chose the same string.
    /// </summary>
    Task<PickListRefillSnapshot?> FindByIdempotencyKeyAsync(
        string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>
    /// Records the batch: one <c>MachineRefill</c> stock movement per row, negative by the units
    /// taken, with the machine and note on it, plus the batch row that holds the key and the
    /// fingerprint. It saves inside the caller's transaction and deliberately does <b>not</b>
    /// commit: the caller opened the transaction and owns the commit, so a refusal after this point
    /// discards the whole batch.
    /// </summary>
    Task<PickListRefillRecord> ApplyAsync(PickListRefillClaim claim, CancellationToken cancellationToken);

    /// <summary>
    /// Whether <paramref name="exception"/> is the database refusing a second refill under one
    /// idempotency key through the per-business unique index.
    ///
    /// <para>It is answered by the adapter because the exception is provider-specific and a
    /// provider's exception type must not cross into the Application layer (docs/architecture.md §
    /// Domain and application error mapping). The use case asks rather than catching, so the index
    /// violation becomes the same stable refusal the key claim produces, while any other database
    /// failure keeps propagating untranslated. It is the last line of defence behind the claim: a
    /// database this process did not serialise with would otherwise surface a <c>500</c> for a
    /// refusal the contract answers with <c>409</c>.</para>
    /// </summary>
    bool IsDuplicateIdempotencyKey(Exception exception);
}
