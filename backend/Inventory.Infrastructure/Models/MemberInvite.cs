using System.Text.Json.Serialization;
using Inventory.Domain.Tenancy;

namespace Inventory.Infrastructure.Models;

/// <summary>
/// One single-use invite link into one business (issue #531, a slice of #504 under #328): the role
/// it grants, who created it, when it expires, and what became of it.
///
/// <para><strong>Tenant-owned, like ordinary business data.</strong> It implements
/// <see cref="IBusinessOwned"/>, so the <c>AppDbContext</c> query filters scope every read of it and
/// <c>BusinessOwnershipEnforcer</c> stamps the owner on insert and keeps it immutable afterwards.
/// That is the opposite of <see cref="BusinessMembership"/>, which carries no filter because it is
/// what resolves the boundary: an invite is not part of resolving anything, it is a row inside one
/// business that only that business's Owners may see.</para>
///
/// <para><strong>The token is stored only as a hash</strong>
/// (<see cref="Inventory.Domain.Tenancy.MemberInviteToken"/>), and the plaintext never reaches this
/// entity, this table or a log. A database copy - a backup, a restored snapshot - therefore yields
/// no usable invite link. <see cref="TokenHash"/> additionally carries
/// <see cref="JsonIgnoreAttribute"/>, as the Nayax credential columns do: there is no DTO over this
/// entity, and the one value that must stay server-side must not become part of an API response
/// because somebody added a convenient projection later.</para>
///
/// <para><strong>The status column is only ever <c>Pending</c>, <c>Used</c> or
/// <c>Cancelled</c>.</strong> <see cref="MemberInviteStatus.Expired"/> is derived from
/// <see cref="ExpiresAtUtc"/> by <c>MemberInvitePolicy</c>, so an invite stops being usable the
/// instant it expires, with no sweep to run and no row left claiming otherwise.</para>
/// </summary>
public class MemberInvite : IBusinessOwned
{
    /// <summary>The invite's own key, which is what the cancel endpoint takes.</summary>
    public int Id { get; set; }

    /// <summary>
    /// The owning business (issue #64). Stamped from the caller's resolved business on insert and
    /// immutable afterwards; never read from API input, and never serialised out.
    /// </summary>
    [JsonIgnore]
    public int BusinessId { get; set; }

    /// <summary>
    /// The role the redeemed membership will be given, stored as the constrained
    /// <see cref="BusinessRole"/> vocabulary rather than a free-text name, exactly as
    /// <see cref="BusinessMembership.Role"/> is - so a typo cannot become a role. A stored value
    /// this code does not declare is reported as unknown and never guessed at.
    /// </summary>
    public BusinessRole Role { get; set; }

    /// <summary>
    /// The Owner's own note - "new site manager", "replacement for the Tuesday run" - so a list of
    /// invites is recognisable. Optional, bounded, display-only, and deliberately never audited or
    /// logged: it is free text that may well name the person being invited.
    /// </summary>
    public string? Note { get; set; }

    /// <summary>
    /// The SHA-256 digest of the invite token, as lowercase hexadecimal. Never a plaintext token,
    /// and never projected into a DTO, a log or a report.
    /// </summary>
    [JsonIgnore]
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>
    /// What became of the invite. See the remarks on this class: <c>Expired</c> is never written.
    /// </summary>
    public MemberInviteStatus Status { get; set; } = MemberInviteStatus.Pending;

    /// <summary>The Entra directory tenant id (<c>tid</c>) of the Owner who created it.</summary>
    public string CreatedByDirectoryTenantId { get; set; } = string.Empty;

    /// <summary>The Entra object id (<c>oid</c>) of the Owner who created it.</summary>
    public string CreatedByObjectId { get; set; } = string.Empty;

    /// <summary>When the invite was created.</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>
    /// When it stops being usable: seven days after creation, fixed by
    /// <c>MemberInvitePolicy.Lifetime</c> and never chosen by a request.
    /// </summary>
    public DateTime ExpiresAtUtc { get; set; }

    /// <summary>When it was redeemed, or <c>null</c>. Written by the redemption of #532.</summary>
    public DateTime? UsedAtUtc { get; set; }

    /// <summary>When it was cancelled, or <c>null</c>.</summary>
    public DateTime? CancelledAtUtc { get; set; }

    /// <summary>
    /// The membership the redemption created, or <c>null</c> while the invite is unused. It is the
    /// link between an invite and the access it granted, which is what makes "how did this person
    /// get in?" answerable.
    ///
    /// A real foreign key to <see cref="BusinessMembership"/>, restricted on delete, so the column
    /// cannot name a membership that does not exist. The *business* side of that relationship is
    /// not something a constraint can express - the referenced membership must also belong to this
    /// invite's business - and it is guaranteed instead by the only path that writes this column
    /// (#532), which stores the membership it has just created in the invite's own business and
    /// transaction.
    /// </summary>
    public int? RedeemedByMembershipId { get; set; }
}
