using Inventory.Application.Nayax;
using Inventory.Application.Time;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Nayax;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="INayaxConnectionTestDraftStore"/> (issue #536). It lives
/// in Inventory.Infrastructure beside the <see cref="AppDbContext"/> and the
/// <see cref="NayaxConnectionTestDraft"/> entity it reads, like every other EF adapter behind an
/// Application persistence port (docs/architecture.md § Inventory.Infrastructure).
///
/// It adds no business predicate of its own: reads go through the central
/// <see cref="AppDbContext"/> query filter and the insert is stamped by
/// <c>BusinessOwnershipEnforcer</c> on save, so one business can never read, decrypt or replace
/// another's draft. The replacing delete is the one statement worth saying out loud, because
/// <c>ExecuteDelete</c> runs in the database and does not pass through <c>SaveChanges</c>: its
/// <c>WHERE</c> clause still carries the owning business, because EF applies the same query filter
/// to the statement's source query, and this adapter additionally refuses to run at all without a
/// single resolved business - an unscoped context would otherwise be the one way a <c>DELETE</c>
/// here could reach every business's draft.
///
/// Encrypting the token is <see cref="INayaxTokenProtector"/>'s job, exactly as on
/// <see cref="EfNayaxConnectionStore"/>. This adapter never writes a plaintext token to the database
/// and never logs one, and its read deliberately does not project the ciphertext at all.
/// </summary>
public sealed class EfNayaxConnectionTestDraftStore : INayaxConnectionTestDraftStore
{
    private readonly AppDbContext _db;
    private readonly INayaxTokenProtector _protector;
    private readonly IClock _clock;

    public EfNayaxConnectionTestDraftStore(AppDbContext db, INayaxTokenProtector protector, IClock clock)
    {
        _db = db;
        _protector = protector;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task<StoredNayaxConnectionTestDraft?> FindAsync(
        Guid draftId,
        ActorIdentity actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (_db.CurrentBusinessId is null)
        {
            // No current business means no draft of one, whether the caller's membership was denied
            // or the context was deliberately constructed unscoped. Reporting none is the
            // fail-closed answer.
            return null;
        }

        var now = _clock.UtcNow;

        // Every condition is part of one query, and all four answer the same "no such draft": the
        // id, the creating actor, and the expiry. The owning business is in the same WHERE clause,
        // contributed by the central query filter rather than by a predicate written here. The
        // ciphertext and the key id are not projected - proving a draft exists must not decrypt it.
        return await _db.NayaxConnectionTestDrafts
            .AsNoTracking()
            .Where(draft => draft.Id == draftId
                && draft.CreatedByDirectoryTenantId == actor.DirectoryTenantId
                && draft.CreatedByObjectId == actor.ObjectId
                && draft.ExpiresAt > now)
            .Select(draft => new StoredNayaxConnectionTestDraft(
                draft.Id,
                draft.OperatorId,
                draft.ResultsJson,
                draft.CreatedAt,
                draft.ExpiresAt))
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ClaimedNayaxConnectionTestDraft?> TryClaimAsync(
        Guid draftId,
        ActorIdentity actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);

        var businessId = RequireCurrentBusinessId("claimed");

        // As above (issue #681): a claim is a DELETE, and a DELETE bypasses SaveChanges.
        SetBasedWriteEnforcer.RequireWritableScope(_db);

        var now = _clock.UtcNow;

        // Every condition of the claim, written once and used by both statements below: the id, the
        // creating actor and the expiry, with the owning business contributed by the central query
        // filter rather than by a predicate written here.
        var claimable = _db.NayaxConnectionTestDrafts
            .Where(draft => draft.Id == draftId
                && draft.CreatedByDirectoryTenantId == actor.DirectoryTenantId
                && draft.CreatedByObjectId == actor.ObjectId
                && draft.ExpiresAt > now);

        // The ciphertext is read first because the delete cannot return it, and then the delete -
        // not this read - is what decides whether the claim succeeded. That order matters: a caller
        // that trusted the read would hand the same credential to two concurrent saves, while a
        // caller that trusts the delete cannot, because only one `DELETE` can remove a row.
        var stored = await claimable
            .AsNoTracking()
            .Select(draft => new
            {
                draft.OperatorId,
                draft.AccessTokenCiphertext,
                draft.EncryptionKeyId,
                draft.ResultsJson,
            })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (stored is null)
        {
            return null;
        }

        // Decrypted before the row is removed, so a key-configuration failure claims nothing at all
        // rather than consuming a draft it cannot hand back. Throws for a corrupt ciphertext, an
        // unconfigured key or a ciphertext encrypted for another business (issue #681), exactly as
        // the connection store does: an undecryptable credential must never look like an absent
        // draft. The business id comes from the context's own tenancy scope - the same scope whose
        // query filter selected the row - and never from a request.
        var accessToken = _protector.Unprotect(
            new ProtectedNayaxToken(stored.EncryptionKeyId, stored.AccessTokenCiphertext),
            BusinessId.From(businessId));

        var claimed = await claimable.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        if (claimed != 1)
        {
            // Another save claimed it between the read and this statement, or the row expired under
            // the same predicate. Either way this caller has no draft, and reporting one would let
            // a test result be accepted twice.
            return null;
        }

        return new ClaimedNayaxConnectionTestDraft(
            // Revision 0: nothing has stored these credentials yet, so there is no revision to
            // attribute a result to. The save's own write is what produces one.
            new NayaxConnectionCredential(stored.OperatorId, accessToken, credentialRevision: 0),
            stored.ResultsJson);
    }

    /// <inheritdoc />
    public async Task<bool> DiscardExpiredAsync(CancellationToken cancellationToken)
    {
        RequireCurrentBusinessId("discarded");

        // The read-only support-session rule, for a DELETE that never reaches
        // BusinessOwnershipEnforcer (issue #681).
        SetBasedWriteEnforcer.RequireWritableScope(_db);

        var now = _clock.UtcNow;

        // One conditional DELETE, with the owning business contributed by the central query filter
        // rather than by a predicate written here, and the expiry boundary matching the one every
        // read and the claim use: a draft is expired *at* its expiry, so the reads' `> now` and this
        // statement's `<= now` partition the table between them and neither can act on a row the
        // other would still honour.
        var discarded = await _db.NayaxConnectionTestDrafts
            .Where(draft => draft.ExpiresAt <= now)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        return discarded > 0;
    }

    /// <inheritdoc />
    public async Task ReplaceAsync(NewNayaxConnectionTestDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var businessId = RequireCurrentBusinessId();

        // As above (issue #681): the replacing DELETE bypasses SaveChanges, so the read-only
        // support-session rule is applied before anything is encrypted or written.
        SetBasedWriteEnforcer.RequireWritableScope(_db);

        // Encrypted before anything is written, so a key-configuration failure stores nothing at all
        // rather than replacing a usable draft with a row holding an empty token. Bound to this
        // business (issue #681), so the draft's ciphertext cannot be claimed from another
        // business's row.
        var protectedToken = _protector.Protect(draft.Credential.AccessToken, BusinessId.From(businessId));

        // The delete and the insert are one change. Without the transaction a failure between them
        // would leave the business with no draft at all, having just discarded a usable one; with it
        // the previous draft survives a failed replacement. A caller that already opened a
        // transaction on this context owns the commit and this join it, as the credential save does.
        await using var transaction = _db.Database.IsRelational() && _db.Database.CurrentTransaction is null
            ? await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;

        // Whichever draft this business held, expired or not: the unique index on the ownership
        // column means the insert below could not land beside it, and leaving a superseded
        // credential ciphertext in the database is exactly what this slice is meant not to do.
        await _db.NayaxConnectionTestDrafts.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

        // BusinessId is left unset on purpose: BusinessOwnershipEnforcer stamps the caller's
        // business on save, which is also what refuses the write if no business is resolved.
        _db.NayaxConnectionTestDrafts.Add(new NayaxConnectionTestDraft
        {
            Id = draft.Id,
            OperatorId = draft.Credential.OperatorId,
            AccessTokenCiphertext = protectedToken.Ciphertext,
            EncryptionKeyId = protectedToken.KeyId,
            ResultsJson = draft.ResultsJson,
            CreatedByDirectoryTenantId = draft.CreatedBy.DirectoryTenantId,
            CreatedByObjectId = draft.CreatedBy.ObjectId,
            CreatedAt = draft.CreatedAtUtc,
            ExpiresAt = draft.ExpiresAtUtc,
        });

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A write needs exactly one resolved business. Both an unresolved caller and a deliberately
    /// unscoped context report none, and neither may write here: <c>ExecuteDelete</c> runs in the
    /// database without passing through <c>BusinessOwnershipEnforcer</c>, so an unscoped statement
    /// would carry no business predicate and reach every business's draft.
    /// </summary>
    /// <param name="operation">What was attempted, for the refusal message.</param>
    /// <returns>
    /// The resolved business's key, which is also what the ciphertext is bound to (issue #681).
    /// </returns>
    private int RequireCurrentBusinessId(string operation = "stored") =>
        _db.CurrentBusinessId
            ?? throw new CrossBusinessAccessException(
                $"A Nayax connection test draft cannot be {operation} without a single resolved "
                    + "current business. The caller has no business membership, or the context was "
                    + "constructed unscoped, so the write is refused.");
}
