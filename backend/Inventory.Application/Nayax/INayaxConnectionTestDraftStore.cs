using Inventory.Domain.Tenancy;

namespace Inventory.Application.Nayax;

/// <summary>
/// A tested credential pair to hold as the current business's draft (issue #536).
///
/// The access token crosses this port in plaintext and the adapter behind it encrypts it, exactly as
/// <see cref="INayaxConnectionStore.SaveCredentialAsync"/> does and for the same reason: keys, key
/// ids and cipher choices stay out of Inventory.Application and Inventory.Domain.
/// </summary>
/// <param name="Id">The opaque draft identifier returned to the caller.</param>
/// <param name="Credential">
/// The tested operator id and token. Carries a secret; the adapter stores only its ciphertext.
/// </param>
/// <param name="ResultsJson">The serialized check report, as the server produced it.</param>
/// <param name="CreatedBy">
/// The Entra <c>(tid, oid)</c> of the Owner who ran the test. The draft is bound to them as well as
/// to the business, so another member of the same business cannot pick it up.
/// </param>
/// <param name="CreatedAtUtc">When the test ran.</param>
/// <param name="ExpiresAtUtc">When the draft stops existing.</param>
public sealed record NewNayaxConnectionTestDraft(
    Guid Id,
    NayaxConnectionCredential Credential,
    string ResultsJson,
    ActorIdentity CreatedBy,
    DateTime CreatedAtUtc,
    DateTime ExpiresAtUtc);

/// <summary>
/// A stored connection-test draft, <strong>without</strong> its token (issue #536).
///
/// Token-free for the reason <see cref="NayaxConnection"/> is: a type that cannot hold the secret
/// cannot leak it into a response body, a log line or a test snapshot. The draft's whole purpose is
/// that the token stays on the server until an Owner saves it, so the read that proves a draft
/// exists must not be the read that decrypts it.
/// </summary>
/// <param name="Id">The draft identifier.</param>
/// <param name="OperatorId">The operator id that was tested. A remote identity, not a secret.</param>
/// <param name="ResultsJson">The serialized check report the test produced.</param>
/// <param name="CreatedAtUtc">When the test ran.</param>
/// <param name="ExpiresAtUtc">When the draft stops existing.</param>
public sealed record StoredNayaxConnectionTestDraft(
    Guid Id,
    string OperatorId,
    string ResultsJson,
    DateTime CreatedAtUtc,
    DateTime ExpiresAtUtc);

/// <summary>
/// Narrow persistence port for the short-lived, encrypted Nayax connection-test draft (issue #536,
/// a slice of #506).
///
/// Like <see cref="INayaxConnectionStore"/>, no member takes a business id: ownership is applied
/// centrally by the <c>AppDbContext</c> tenant query filters and <c>BusinessOwnershipEnforcer</c>,
/// so a business id a method accepted would be a business id some later caller could supply. A
/// caller with no resolved business reads nothing and writes nothing.
///
/// The draft is <strong>not</strong> the stored connection. It holds credentials nothing has
/// accepted yet, it is never read by an ordinary Nayax call, and it disappears on its own: saving it
/// permanently is <see cref="INayaxConnectionStore.SaveCredentialAsync"/>'s job and a separate,
/// reviewed step (#537).
/// </summary>
public interface INayaxConnectionTestDraftStore
{
    /// <summary>
    /// Stores <paramref name="draft"/> as the current business's one draft, discarding whichever
    /// draft it held before.
    ///
    /// Replacement rather than accumulation is the point: a business has at most one draft, so a
    /// draft id stops existing the moment another test runs, and a pile of expired credential
    /// ciphertexts cannot build up. The delete and the insert are one atomic change - a failure
    /// leaves the previous draft in place rather than leaving the business with none.
    /// </summary>
    Task ReplaceAsync(NewNayaxConnectionTestDraft draft, CancellationToken cancellationToken);

    /// <summary>
    /// The current business's draft with this id, as created by this actor and not yet expired, or
    /// <see langword="null"/>.
    ///
    /// It fails closed in every direction and reports one answer for all of them: no such draft, a
    /// draft belonging to another business, a draft another member of the same business created, and
    /// a draft past its expiry all read as "there is no such draft". Distinguishing them would tell a
    /// caller that somebody else's draft exists.
    /// </summary>
    /// <param name="draftId">The opaque identifier the test returned.</param>
    /// <param name="actor">The Entra <c>(tid, oid)</c> the draft must have been created by.</param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    Task<StoredNayaxConnectionTestDraft?> FindAsync(
        Guid draftId,
        ActorIdentity actor,
        CancellationToken cancellationToken);
}
