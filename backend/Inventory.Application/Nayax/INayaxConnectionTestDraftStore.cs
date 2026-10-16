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
/// A draft that one save claimed, <strong>with</strong> its decrypted token (issue #537).
///
/// This is the one read of a draft that handles the secret, and it exists only because accepting a
/// draft means storing the pair it holds. It is returned by
/// <see cref="INayaxConnectionTestDraftStore.TryClaimAsync"/> alone - the claim that also deletes
/// the row - so there is no way to decrypt a draft without consuming it.
///
/// The credential's revision is <c>0</c>: these credentials have never been stored, so there is no
/// revision to attribute a result to yet. The revision the save writes comes from
/// <see cref="INayaxConnectionStore.SaveCredentialAsync"/>, which computes it in the database.
/// </summary>
/// <param name="Credential">
/// The tested operator id and decrypted token. Carries a secret: hand it to the connection store
/// and nowhere else, and never log, serialise or return it.
/// </param>
/// <param name="ResultsJson">The serialized check report the test produced.</param>
public sealed record ClaimedNayaxConnectionTestDraft(
    NayaxConnectionCredential Credential,
    string ResultsJson);

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
    /// Deletes the current business's draft if it has passed its expiry, and answers whether it
    /// deleted one (issue #681).
    ///
    /// <para>An expired draft is already unusable - every read and the claim all match only a draft
    /// whose expiry is still ahead - so this changes no decision. What it changes is how long a
    /// credential ciphertext nobody can use any more stays in the database: without it, a test an
    /// Owner abandoned and a save that was refused both leave their draft sitting there until the
    /// <em>next</em> test replaces it, which may be never. Every caller of this port therefore
    /// discards an expired draft before it does its own work.</para>
    ///
    /// <para>It is called outside a transaction on purpose. The save claims its draft inside one, so
    /// a sweep made there would be rolled back together with the refusal that is exactly the case
    /// worth sweeping.</para>
    ///
    /// <para>It is deliberately only the <em>current</em> business's draft: this port is scoped the
    /// same way every other one is, and a sweep across businesses would need an unscoped context,
    /// which no request path may have (AGENTS.md § Tenant ownership and data isolation).</para>
    /// </summary>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    Task<bool> DiscardExpiredAsync(CancellationToken cancellationToken);

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

    /// <summary>
    /// Claims the current business's draft with this id, created by this actor and not yet expired,
    /// <strong>deleting it in the same statement</strong>, and returns its decrypted credential and
    /// its results - or <see langword="null"/> when there was no such draft to claim (issue #537).
    ///
    /// <para><strong>A claim is single-use, and that is enforced by the delete, not by a check.</strong>
    /// The row is removed by a conditional <c>DELETE</c> carrying the id, the owning business, the
    /// creating actor and the expiry, and the claim succeeds only when that statement removed exactly
    /// one row. Two simultaneous saves of one draft therefore cannot both succeed however they
    /// interleave: the second one's <c>DELETE</c> matches nothing. A test-then-delete would leave
    /// precisely that race open, which is why this is one statement and why the claim - not the read
    /// - is what the caller branches on.</para>
    ///
    /// <para>It fails closed in every direction and reports one answer for all of them, exactly as
    /// <see cref="FindAsync"/> does: no such draft, another business's, another member's, an expired
    /// one, and one a previous save already claimed all read as <see langword="null"/>.</para>
    ///
    /// <para>The caller is expected to run this inside
    /// <see cref="INayaxConnectionSaveTransactions"/>'s transaction together with the connection
    /// write, so a failure to store the credential puts the claimed draft back rather than consuming
    /// a test result that was never accepted.</para>
    /// </summary>
    /// <param name="draftId">The opaque identifier the test returned.</param>
    /// <param name="actor">The Entra <c>(tid, oid)</c> the draft must have been created by.</param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    Task<ClaimedNayaxConnectionTestDraft?> TryClaimAsync(
        Guid draftId,
        ActorIdentity actor,
        CancellationToken cancellationToken);
}
