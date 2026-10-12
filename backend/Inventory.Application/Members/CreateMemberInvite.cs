using System.Buffers.Text;
using System.Security.Cryptography;
using Inventory.Application.Tenancy;
using Inventory.Application.Time;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Tenancy;

namespace Inventory.Application.Members;

/// <summary>
/// Creates one single-use invite link for the current business (issue #531, a slice of #504 under
/// #328).
///
/// <para><strong>Nothing about the owner comes from the request.</strong> The business is resolved
/// from the authenticated actor's membership through <see cref="ICurrentBusinessProvider"/> and the
/// creating identity from <see cref="IAuthenticatedActorAccessor"/>; the request carries the role
/// and an optional note, and nothing else. There is no business id, no creator id and no expiry in
/// the contract - an invite that let its creator choose when it expires would not have a
/// seven-day lifetime. The resolved business is then passed to the store explicitly, because
/// <c>MemberInvite</c> is one of the four structural non-tenant-owned tables and has no central
/// query filter or ownership stamp to defer to (issue #532; docs/architecture.md § Tenant
/// ownership).</para>
///
/// <para><strong>The token is generated here, returned once, and stored only as a hash.</strong>
/// <see cref="MemberInviteToken.EntropyBytes"/> bytes come from the platform's cryptographic random
/// number generator and are rendered URL-safe so they can be part of a link. What crosses the
/// persistence port is <see cref="MemberInviteToken.Hash"/>'s output, so no store, adapter or
/// database row ever sees the plaintext; what crosses the audit port is neither. The Owner either
/// copies the link out of this one response or creates another invite.</para>
///
/// <para><strong>It creates no membership.</strong> An invite is an offer, and redeeming it is
/// #532's separate, reviewed step - which is why this use case does not go through
/// <see cref="MembershipWriteGuard"/>: there is no membership to serialise against, no eligibility
/// rule to re-check and no Owner to retain. Creating an invite for a person who already belongs to
/// a business is therefore accepted; it is the redemption that has to refuse, and it has to refuse
/// on the state at that moment rather than on this one.</para>
/// </summary>
public sealed class CreateMemberInvite
{
    private readonly ICurrentBusinessProvider _currentBusiness;
    private readonly IAuthenticatedActorAccessor _actorAccessor;
    private readonly IMemberInviteStore _store;
    private readonly IMemberInviteAudit _audit;
    private readonly IClock _clock;

    public CreateMemberInvite(
        ICurrentBusinessProvider currentBusiness,
        IAuthenticatedActorAccessor actorAccessor,
        IMemberInviteStore store,
        IMemberInviteAudit audit,
        IClock clock)
    {
        _currentBusiness = currentBusiness;
        _actorAccessor = actorAccessor;
        _store = store;
        _audit = audit;
        _clock = clock;
    }

    /// <summary>
    /// Creates an invite granting <paramref name="role"/>, carrying <paramref name="note"/>.
    /// </summary>
    /// <param name="role">
    /// The role's published <em>name</em>, case-insensitively. Anything else - a blank value, a
    /// number such as <c>40</c>, or a role a newer deployment knows - is refused and nothing is
    /// stored: a stored representation is not something a request supplies.
    /// </param>
    /// <param name="note">The Owner's own note, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancellation for the whole operation.</param>
    /// <returns>The invite, and its token - the only time the token exists outside the request.</returns>
    /// <exception cref="DomainValidationException">
    /// <paramref name="role"/> is not a declared role name, or <paramref name="note"/> is too long.
    /// </exception>
    /// <exception cref="BusinessAccessDeniedException">The caller has no usable membership.</exception>
    public async Task<CreatedMemberInvite> Handle(string? role, string? note, CancellationToken cancellationToken)
    {
        // Both checks run before anything is read, resolved or generated, so an invalid request
        // cannot reach a write, a token or the audit trail.
        if (!BusinessRoles.TryParse(role, out var requested))
        {
            throw new DomainValidationException(BusinessRoles.UnsupportedMessage);
        }

        var normalizedNote = MemberInvitePolicy.NormalizeNote(note);

        var businessId = await _currentBusiness.RequireBusinessIdAsync(cancellationToken)
            .ConfigureAwait(false);
        var actor = _actorAccessor.GetCurrentActor().Actor
            ?? throw new BusinessAccessDeniedException(BusinessAccessDenialReason.NotAuthenticated);

        var createdAtUtc = _clock.UtcNow;

        // URL-safe, so the whole token can sit in a link without being re-encoded on the way, and
        // from the cryptographic generator rather than Random or a Guid: a guessable invite token is
        // a way into somebody else's business.
        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(MemberInviteToken.EntropyBytes));

        var stored = await _store
            .CreateAsync(
                businessId,
                new NewMemberInvite(
                    requested,
                    normalizedNote,
                    MemberInviteToken.Hash(token),
                    actor,
                    createdAtUtc,
                    MemberInvitePolicy.ExpiresAt(createdAtUtc)),
                cancellationToken)
            .ConfigureAwait(false);

        // After the write, because an invite that was not stored was not created. The event carries
        // the role and never the token or the note.
        _audit.Record(new MemberInviteAuditEntry(
            actor.DirectoryTenantId,
            actor.ObjectId,
            businessId.Value,
            stored.InviteId,
            MemberInviteChange.Created,
            requested,
            createdAtUtc));

        return new CreatedMemberInvite(MemberInvite.From(stored, createdAtUtc), token);
    }
}
