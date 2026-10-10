using Inventory.Domain.Nayax;

namespace Inventory.Application.Nayax;

/// <summary>
/// Narrow persistence port for the current business's own Nayax connection (issue #518, a slice of
/// #500).
///
/// Every member works on the business the caller already resolved: no method takes a business id,
/// because ownership is applied centrally by the <c>AppDbContext</c> tenant query filters and
/// <c>BusinessOwnershipEnforcer</c>, and a business id that a method accepted would be a business
/// id some later caller could supply. A caller with no resolved business reads nothing and writes
/// nothing.
///
/// Encryption is not part of this port. The token crosses it in plaintext, inside
/// <see cref="NayaxConnectionCredential"/>, and the adapter behind it owns protecting it at rest -
/// which is what keeps Key Vault, key ids and cipher choices out of Inventory.Application and
/// Inventory.Domain entirely.
/// </summary>
public interface INayaxConnectionStore
{
    /// <summary>
    /// The current business's connection without its token, or <see langword="null"/> when the
    /// business has never stored credentials.
    /// </summary>
    Task<NayaxConnection?> FindAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The current business's operator id and decrypted access token, or <see langword="null"/>
    /// when it has stored none.
    ///
    /// Separate from <see cref="FindAsync"/> on purpose: only a caller that actually has to
    /// authenticate to Nayax asks for the secret, so a status read cannot carry a token by
    /// accident. It fails closed rather than degrading - a ciphertext that cannot be decrypted,
    /// because it was tampered with or because the key that encrypted it is not configured, throws
    /// instead of returning a connection with no usable token.
    /// </summary>
    /// <exception cref="Exception">
    /// Decryption failed. The adapter's exception type is an Infrastructure concern; what this port
    /// guarantees is that an undecryptable credential is never silently reported as absent or
    /// empty.
    /// </exception>
    Task<NayaxConnectionCredential?> FindCredentialAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The current business's status and credential as one consistent snapshot - both read from the
    /// same row in the same statement - or <see langword="null"/> when the business has stored no
    /// connection or none could be scoped to it.
    ///
    /// This is what an ordinary Nayax call resolves its credentials through, rather than
    /// <see cref="FindAsync"/> followed by <see cref="FindCredentialAsync"/>: a credential save
    /// landing between those two reads would hand the caller the previous status beside the new
    /// token, and that pair decides both whether the call may run and how a Nayax <c>403</c> is
    /// classified. One read cannot produce a pair that never existed.
    ///
    /// <paramref name="mayDecryptToken"/> is the caller's gate, evaluated against the status in that
    /// same snapshot and *before* the token is decrypted: a refused status returns a snapshot whose
    /// <see cref="NayaxConnectionSnapshot.Credential"/> is <see langword="null"/> and never touches
    /// the ciphertext. Passing the gate into the read is what keeps "which statuses may be used" a
    /// single Domain rule the caller owns while still resolving everything in one statement.
    ///
    /// Decryption fails closed exactly as in <see cref="FindCredentialAsync"/>.
    /// </summary>
    /// <param name="mayDecryptToken">
    /// Whether the token may be decrypted for the status it is stored with -
    /// <see cref="Domain.Nayax.NayaxConnectionStatusGate.AllowsOrdinaryOperations"/> for an ordinary
    /// call.
    /// </param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    /// <exception cref="Exception">
    /// Decryption failed; see <see cref="FindCredentialAsync"/>.
    /// </exception>
    Task<NayaxConnectionSnapshot?> FindForOperationAsync(
        Func<NayaxConnectionStatus, bool> mayDecryptToken,
        CancellationToken cancellationToken);

    /// <summary>
    /// Stores a new operator id and access token for the current business, creating the record at
    /// revision 1 when there is none.
    ///
    /// A save always supersedes what was there: the credential revision increments, the status
    /// returns to <see cref="Domain.Nayax.NayaxConnectionStatus.PendingPermissions"/> and the
    /// last-tested instant is cleared, so a newly stored token can never inherit the previous
    /// token's test result.
    ///
    /// A save is atomic, and it takes part in a transaction already in progress on the same unit of
    /// work rather than committing on its own, so a save and the status write that follows it can be
    /// one change that either lands completely or not at all.
    /// </summary>
    /// <param name="operatorId">The Nayax operator id the token belongs to.</param>
    /// <param name="accessToken">The Lynx API access token to store encrypted.</param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    /// <returns>The stored connection, including its new revision. Never includes the token.</returns>
    Task<NayaxConnection> SaveCredentialAsync(
        string operatorId,
        string accessToken,
        CancellationToken cancellationToken);

    /// <summary>
    /// Applies a permission test result to the current business's connection, but only while the
    /// stored credential revision still matches the one the result was produced for.
    ///
    /// The comparison and the write are one conditional statement, not a read followed by a write:
    /// the result is written by an <c>UPDATE</c> whose <c>WHERE</c> clause carries both the owning
    /// business and the expected revision. A result produced for superseded credentials therefore
    /// matches no row and is discarded rather than overwriting the status of the credentials that
    /// replaced them.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the result was applied; <see langword="false"/> when it was
    /// discarded as stale, which the adapter also records as a structured log event.
    /// </returns>
    Task<bool> TryApplyStatusResultAsync(
        NayaxConnectionStatusResult result,
        CancellationToken cancellationToken);
}
