namespace InventoryApi.DTOs;

/// <summary>
/// One invite link of the signed-in Owner's business, as <c>GET /api/members/invites</c> and the
/// cancel endpoint return them (issue #531).
///
/// It carries no token, in plaintext or hashed: the plaintext exists only in the creation response
/// below, and the hash is the one value that stays in the database. It carries no business
/// identifier either - the business is resolved from the caller's own membership - and nothing
/// about the Owner who created the invite, for the reason <see cref="BusinessMemberResponse"/>
/// publishes no Entra <c>(tid, oid)</c> pair.
/// </summary>
/// <param name="InviteId">The invite's key within the caller's business, which the cancel endpoint takes.</param>
/// <param name="Role">
/// <c>Owner</c>, <c>Manager</c> or <c>Operator</c> - the published role name rather than a number a
/// client would have to map - or <see langword="null"/> when the stored value is one this
/// deployment does not declare.
/// </param>
/// <param name="Note">The Owner's own note, or <see langword="null"/> when they wrote none.</param>
/// <param name="Status">
/// <c>Pending</c>, <c>Used</c>, <c>Expired</c> or <c>Cancelled</c>, judged at the instant of the
/// read: a pending invite past its expiry reports <c>Expired</c> without any row having changed.
/// <see langword="null"/> when the stored state is one this deployment does not declare - never
/// <c>Pending</c>, which is the one state that says an invite may still be used.
/// </param>
/// <param name="CreatedAtUtc">When the invite was created.</param>
/// <param name="ExpiresAtUtc">When it stops being usable: seven days after creation.</param>
/// <param name="UsedAtUtc">When it was redeemed, or <see langword="null"/>.</param>
/// <param name="CancelledAtUtc">When it was cancelled, or <see langword="null"/>.</param>
/// <param name="RedeemedByMembershipId">
/// The membership the redemption created, or <see langword="null"/>. A membership key within the
/// caller's own business, exactly as the members list publishes.
/// </param>
public sealed record MemberInviteResponse(
    int InviteId,
    string? Role,
    string? Note,
    string? Status,
    DateTime CreatedAtUtc,
    DateTime ExpiresAtUtc,
    DateTime? UsedAtUtc,
    DateTime? CancelledAtUtc,
    int? RedeemedByMembershipId);

/// <summary>
/// The body of <c>POST /api/members/invites</c>'s response (issue #531): the invite, and its token -
/// once.
///
/// <para>This is the only response in the application that carries the token, and it is the only
/// time it exists outside the request that generated it: the database holds a hash, no log or audit
/// event carries it, and no later read can produce it. An Owner either copies the link out of this
/// response or creates another invite. The link itself is composed by the Owner-facing page (#505)
/// from this token, because the API has no authoritative knowledge of the web application's
/// address - a configured public base URL would be deployment configuration, decided outside this
/// issue.</para>
/// </summary>
/// <param name="Token">The plaintext invite token, URL-safe so it can be part of a link.</param>
/// <param name="Invite">The invite as it now stands.</param>
public sealed record CreatedMemberInviteResponse(string Token, MemberInviteResponse Invite);

/// <summary>
/// The body of <c>POST /api/members/invites</c> (issue #531).
///
/// Two fields, and neither of them is an owner, an expiry or a token. The business comes from the
/// caller's membership, the expiry from the fixed seven-day lifetime, and the token from the
/// server's cryptographic random number generator - an invite whose creator could choose any of
/// those three would not be the invite this contract describes.
/// </summary>
/// <param name="Role">
/// <c>Owner</c>, <c>Manager</c> or <c>Operator</c>, case-insensitively. Anything else - including a
/// blank value, a number, or a role a newer deployment knows - is rejected with <c>400</c> and
/// nothing is stored.
/// </param>
/// <param name="Note">
/// An optional note of up to 200 characters, so an Owner can recognise their own invite in a list.
/// It is display-only: it is never logged, never audited, and nothing looks an invite up by it.
/// </param>
public sealed record CreateMemberInviteRequest(string? Role, string? Note);
