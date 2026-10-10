namespace Inventory.Application.Nayax;

/// <summary>
/// The gate-free credential read behind <see cref="INayaxConnectionRetestCredentialProvider"/>
/// (issue #520), for the Owner-only re-test of issue #506.
///
/// It is a separate type from <see cref="NayaxRequestCredentialProvider"/> rather than a second
/// method on it so the boundary can be enforced structurally: nothing an ordinary consumer can
/// reach - neither the gated provider nor its interface - offers a way to read a token the status
/// gate refuses.
///
/// It is this thin on purpose. Reading the current business's credential regardless of status is
/// exactly <see cref="INayaxConnectionStore.FindCredentialAsync"/>, including its fail-closed
/// behaviour when a ciphertext cannot be decrypted; what this type adds is a named, separately
/// registered port that only the re-test use case may depend on. Keeping it here, rather than
/// letting #506 take the store directly, is what keeps "may read a token the gate refuses" a
/// reviewable list of one.
/// </summary>
public sealed class NayaxConnectionRetestCredentialProvider : INayaxConnectionRetestCredentialProvider
{
    private readonly INayaxConnectionStore _store;

    public NayaxConnectionRetestCredentialProvider(INayaxConnectionStore store)
    {
        _store = store;
    }

    /// <inheritdoc />
    public Task<NayaxConnectionCredential?> GetForRetestAsync(CancellationToken cancellationToken) =>
        _store.FindCredentialAsync(cancellationToken);
}
