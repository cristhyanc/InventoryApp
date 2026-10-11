namespace InventoryApi.DTOs;

/// <summary>
/// One member of the signed-in Owner's business, as <c>GET /api/members</c> and the three change
/// endpoints return them (issue #533).
///
/// It carries no business identifier and no Entra <c>(tid, oid)</c> pair. The business is resolved
/// from the caller's own membership and no client has a use for its key; the claim pair is the
/// identity every access decision is made from, and publishing it would put that one value into a
/// response a later request could try to send back. <see cref="MembershipId"/> is what the change
/// endpoints take, and it is only ever accepted within the caller's own business.
/// </summary>
/// <param name="MembershipId">The membership's key within the caller's business.</param>
/// <param name="DisplayName">
/// The member's display name, or <see langword="null"/> when no sign-in token has reported one.
/// Serialized as an explicit <c>null</c> rather than omitted, so the shape is stable.
/// </param>
/// <param name="Email">The member's email address on the same terms; a mailbox to show, never an identifier.</param>
/// <param name="Role">
/// <c>Owner</c>, <c>Manager</c> or <c>Operator</c> - the published role name rather than a number
/// a client would have to map and a renumbering would silently change - or <see langword="null"/>
/// when the stored value is one this deployment does not declare, which already denies that member
/// access everywhere.
/// </param>
/// <param name="IsActive">Whether the approval is current. A revoked member is listed as inactive.</param>
/// <param name="JoinedAtUtc">When the membership was created.</param>
public sealed record BusinessMemberResponse(
    int MembershipId,
    string? DisplayName,
    string? Email,
    string? Role,
    bool IsActive,
    DateTime JoinedAtUtc);

/// <summary>
/// The body of <c>PUT /api/members/{membershipId}/role</c> (issue #533).
///
/// One field, and it is the role's published <em>name</em>. A numeric spelling is refused with the
/// rest of the unsupported values: a stored representation is not something a request gets to
/// supply.
/// </summary>
/// <param name="Role">
/// <c>Owner</c>, <c>Manager</c> or <c>Operator</c>, case-insensitively. Anything else - including a
/// blank value, a number, or a role a newer deployment knows - is rejected with <c>400</c> and
/// nothing is stored.
/// </param>
public sealed record ChangeMemberRoleRequest(string? Role);
