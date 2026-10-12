namespace Inventory.Application.Nayax;

/// <summary>
/// The database transaction one save-from-draft runs in (issue #537). Disposing it without
/// <see cref="CommitAsync"/> rolls back everything the save wrote through the two Nayax connection
/// ports, which is what makes claiming a draft and storing its credential one change.
/// </summary>
public interface INayaxConnectionSaveTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Begins the transaction a Nayax connection save runs in (issue #537), so the draft claim and the
/// credential write land together or not at all.
///
/// <para>It is its own narrow port, named for the one operation that needs it, rather than a method
/// on either store. The transaction spans <em>both</em> of them -
/// <see cref="INayaxConnectionTestDraftStore.TryClaimAsync"/> and
/// <see cref="INayaxConnectionStore.SaveCredentialAsync"/> - so putting it on one would imply the
/// other's writes were outside it. Which unit of work the adapters share is an Infrastructure fact;
/// what the Application layer states is that these writes are one operation.</para>
///
/// <para>Why it matters is the half-done state it rules out: a draft consumed with no credential
/// stored would destroy a tested token the Owner can no longer save, and a credential stored with
/// the draft still there would leave a second decryptable copy of it in the database and let the same
/// test result be saved twice.</para>
/// </summary>
public interface INayaxConnectionSaveTransactions
{
    /// <summary>Begins the transaction the save's claim and credential write run in.</summary>
    Task<INayaxConnectionSaveTransaction> BeginAsync(CancellationToken cancellationToken);
}
