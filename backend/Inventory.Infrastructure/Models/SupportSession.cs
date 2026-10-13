using System.Text.Json.Serialization;

namespace Inventory.Infrastructure.Models;

/// <summary>
/// One time-limited, audited support session: a platform administrator allowed to <em>read</em> one
/// business's data for an hour (issue #550, a slice of #512 under #328).
///
/// <para><strong>Structurally non-tenant-owned, on <see cref="BusinessMembership"/>'s and
/// <see cref="MemberInvite"/>'s pattern</strong>, and it is the fifth such table. It deliberately does
/// not implement <see cref="IBusinessOwned"/>, so it carries no global query filter and
/// <c>BusinessOwnershipEnforcer</c> never stamps it: the boundary for this one table is the
/// <see cref="BusinessId"/> named in every statement, which only ever comes from the server's own side
/// of the operation.
///
/// The structural reason is who writes it. Every request that reaches this table is a platform-admin
/// request, and a platform administrator is a member of no business: their request resolves nothing
/// and carries a <em>denied</em> scope. Under a denied scope the central filter matches no row and the
/// ownership stamp has no business to stamp, so a tenant-owned session table could be neither written
/// nor read by the only caller it exists for - and the business cannot come from the request either
/// (AGENTS.md § Tenant ownership). It therefore comes from the business row the start transaction
/// read, or from the row a deactivation read inside its own transaction. A filter that the one path
/// the table exists for must evade is not a boundary; naming the owner in the statement is.
///
/// What this does <em>not</em> relax: no request value may choose the business, the
/// <see cref="BusinessId"/> is never written after the insert, every read and write names it or the
/// actor pair, and nothing tenant-owned becomes reachable. The session is a record that access was
/// granted; it is not itself a way to read anything, and (until issue #551) nothing in the request
/// pipeline reads it at all.</para>
///
/// <para><strong>The session id is stored only as a hash</strong>
/// (<see cref="Inventory.Domain.Tenancy.SupportSessionId"/>), and the plaintext never reaches this
/// entity, this table or a log. A database copy - a backup, a restored snapshot - therefore yields no
/// usable session id. <see cref="SessionIdHash"/> additionally carries
/// <see cref="JsonIgnoreAttribute"/>, as the Nayax credential columns and the invite's token hash do:
/// there is no DTO over this entity, and the one value that must stay server-side must not become part
/// of an API response because somebody added a convenient projection later.</para>
///
/// <para><strong><see cref="EndedAtUtc"/> is only ever written by a decision.</strong> A session that
/// merely ran out keeps it null: expiry is derived from <see cref="ExpiresAtUtc"/> by
/// <c>SupportSessionPolicy.IsActive</c>, so a session stops being usable at the instant it expires
/// with no sweep to run and no row left claiming otherwise. The column therefore means "somebody
/// closed this" - the administrator's own end call, or the deactivation of the business - and never
/// "this is over".</para>
/// </summary>
public class SupportSession
{
    /// <summary>
    /// The row's own key. It is an internal identifier, never the session id: it identifies the
    /// session in the audit trail, and no request can present it to join a session.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// The business the session may read (issue #64). Written once, from the business row the start
    /// transaction read, and never changed afterwards: no path updates this column. Never read from
    /// API input, and never serialised out.
    /// </summary>
    [JsonIgnore]
    public int BusinessId { get; set; }

    /// <summary>
    /// The SHA-256 digest of the session id, as lowercase hexadecimal. Never a plaintext id, and
    /// never projected into a DTO, a log, an audit event or a report.
    /// </summary>
    [JsonIgnore]
    public string SessionIdHash { get; set; } = string.Empty;

    /// <summary>
    /// The Entra directory tenant id (<c>tid</c>) of the platform administrator the session belongs
    /// to. Together with <see cref="ActorObjectId"/> it is what every read of this table matches on:
    /// a session is only ever visible, usable and endable by the one actor it was granted to.
    /// </summary>
    public string ActorDirectoryTenantId { get; set; } = string.Empty;

    /// <summary>The Entra object id (<c>oid</c>) of that administrator.</summary>
    public string ActorObjectId { get; set; } = string.Empty;

    /// <summary>
    /// Why the administrator needed to look, as they typed it. Required and bounded, and the one
    /// free-text value this feature's audit trail deliberately carries: a support session is somebody
    /// outside a business reading its data, so "why" is what makes the access reviewable.
    /// </summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>When the session started.</summary>
    public DateTime StartedAtUtc { get; set; }

    /// <summary>
    /// When it stops being usable: 60 minutes after it started, fixed by
    /// <c>SupportSessionPolicy.Lifetime</c> and never chosen or extended by a request.
    /// </summary>
    public DateTime ExpiresAtUtc { get; set; }

    /// <summary>
    /// When something closed the session, or <c>null</c>. See the remarks on this class: an expired
    /// session is never written here.
    /// </summary>
    public DateTime? EndedAtUtc { get; set; }
}
