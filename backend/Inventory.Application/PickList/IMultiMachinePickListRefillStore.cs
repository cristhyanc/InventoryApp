namespace Inventory.Application.PickList;

/// <summary>
/// Narrow persistence port for the multi-machine refill batch (issue #897), owned by the Application
/// layer and deliberately separate from <see cref="IPickListRefillStore"/>: the two use cases record
/// different things, so each keeps a port that says only what its own use case needs. They share the
/// write transaction type, because they share the write lock and the stored idempotency key namespace
/// - a key may record one kind of refill or the other, never both.
///
/// <para>It owns no rule. The validation is
/// <c>Inventory.Domain.Stock.MultiMachinePickListRefillPolicy</c>'s, the facts it is decided from are
/// read through the Pick List's existing storage-stock port and the machines port, and the costing of
/// each movement is the established <c>IRecordInventoryMovement</c>/<c>IRebuildProductCost</c> pair's
/// - an allocation records exactly the movement the single-product action already records, so its
/// costing and COGS behaviour is unchanged by construction.</para>
/// </summary>
public interface IMultiMachinePickListRefillStore
{
    /// <summary>
    /// Opens the write transaction the whole batch runs in. Every read that decides the write - the
    /// key claim, the machines and the storage quantities - must happen inside it. On SQLite it is
    /// the same <c>BEGIN IMMEDIATE</c> transaction as
    /// <see cref="IPickListRefillStore.BeginWriteTransactionAsync"/>, over the same table, so a
    /// single-machine refill and a multi-machine batch serialise against each other as well as
    /// against their own kind.
    /// </summary>
    Task<IPickListRefillWriteTransaction> BeginWriteTransactionAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The multi-machine batch already recorded under <paramref name="idempotencyKey"/> for the
    /// caller's business, or <see langword="null"/> when that key has recorded nothing. Scoped by the
    /// ordinary tenant query filters, so one business's key never finds another's refill even when
    /// both chose the same string.
    ///
    /// <para>A key that recorded a <em>single-machine</em> refill is found too, with the fingerprint
    /// that refill stored: the fingerprints of the two contracts can never match
    /// (<c>MultiMachinePickListRefillFingerprint</c> renders its own version), so the use case refuses
    /// such a key as reused rather than replaying a refill this contract did not record.</para>
    /// </summary>
    Task<MultiMachinePickListRefillSnapshot?> FindByIdempotencyKeyAsync(
        string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>
    /// Records the batch: one <c>MachineRefill</c> stock movement per allocation, negative by the
    /// units taken, attributed to that allocation's own local machine, plus the batch row that holds
    /// the key and the fingerprint. It saves inside the caller's transaction and deliberately does
    /// <b>not</b> commit: the caller opened the transaction and owns the commit, so a refusal after
    /// this point discards the whole batch.
    /// </summary>
    Task<MultiMachinePickListRefillRecord> ApplyAsync(
        MultiMachinePickListRefillClaim claim, CancellationToken cancellationToken);

    /// <summary>
    /// Whether <paramref name="exception"/> is the database refusing a second refill under one
    /// idempotency key through the per-business unique index.
    ///
    /// <para>It is answered by the adapter because the exception is provider-specific and a provider's
    /// exception type must not cross into the Application layer (docs/architecture.md § Domain and
    /// application error mapping). The use case asks rather than catching, so the index violation
    /// becomes the same stable refusal the key claim produces, while any other database failure keeps
    /// propagating untranslated.</para>
    /// </summary>
    bool IsDuplicateIdempotencyKey(Exception exception);
}
