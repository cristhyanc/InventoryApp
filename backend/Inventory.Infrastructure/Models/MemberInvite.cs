using System.Text.Json.Serialization;
using Inventory.Domain.Tenancy;

namespace Inventory.Infrastructure.Models;

/// <summary>
/// One single-use invite link into one business (issue #531, a slice of #504 under #328): the role
/// it grants, who created it, when it expires, and what became of it.
///
/// <para><strong>Structurally non-tenant-owned, on <see cref="BusinessMembership"/>'s pattern</strong>
/// (reviewed decision on issue #532, replacing issue #531's tenant-owned stamp). It deliberately
/// does <em>not</em> implement <see cref="IBusinessOwned"/>, so it carries no global query filter and
/// <c>BusinessOwnershipEnforcer</c> never stamps it: the boundary for this one table is the
/// <see cref="BusinessId"/> named in every statement, which only ever comes from tenancy resolution
/// (the Owner-facing create, list and cancel) or from the invite row itself (the redemption).
///
/// The structural reason is the redemption. A person arriving with an invite link has no membership,
/// so their request resolves no business at all and carries a <em>denied</em> scope - and under a
/// denied scope the central filter matches no row, which would make an invite impossible to find by
/// the one thing the caller actually presents, its token. The business cannot come from the request
/// either (AGENTS.md § Tenant ownership), so it has to come from the row the token identifies. A
/// filter that the one path the table exists for must evade is not a boundary; naming the owner in
/// the statement is. The invite is therefore listed in
/// <c>BusinessOwnershipCoverageTests.DeliberatelyGlobalEntities</c> with that reason, and it is the
/// fourth such table rather than a new class of exception.
///
/// What this does <em>not</em> relax: no request value may choose the business, the
/// <see cref="BusinessId"/> is never written after the insert, every read and write names it, and
/// one business can therefore still neither read, cancel nor replace another's invite.</para>
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
public class MemberInvite
{
    /// <summary>The invite's own key, which is what the cancel endpoint takes.</summary>
    public int Id { get; set; }

    /// <summary>
    /// The owning business (issue #64). Written once, from the business tenancy resolution produced
    /// for the creating Owner, and never changed afterwards: no path updates this column, and the
    /// redemption's statements take it from the invite row they found. Never read from API input,
    /// and never serialised out.
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
