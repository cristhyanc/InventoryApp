using System.Text.Json.Serialization;
using Inventory.Domain.Tenancy;

namespace Inventory.Infrastructure.Models;

/// <summary>
/// Approves one authenticated Microsoft Entra actor, identified by the validated
/// <c>(tid, oid)</c> claim pair, for one <see cref="Models.Business"/> (issue #64).
///
/// This table is the application's authorization boundary for data ownership: an actor with no
/// active row here reads no business data, whatever Entra directory issued a valid token. Rows
/// are created by an explicit, human-supplied bootstrap, never by self-service sign-up.
///
/// It deliberately stores no email address, display name, or other personal identifier: those
/// are mutable and reassignable, so they must not decide ownership, and keeping them out avoids
/// putting personal data in the repository's schema.
/// </summary>
public class BusinessMembership
{
    public int Id { get; set; }

    public int BusinessId { get; set; }

    [JsonIgnore]
    public virtual Business? Business { get; set; }

    /// <summary>The Entra directory tenant id (<c>tid</c>) the actor signs in from.</summary>
    public string DirectoryTenantId { get; set; } = string.Empty;

    /// <summary>The actor's stable Entra object id (<c>oid</c>) within that directory.</summary>
    public string ObjectId { get; set; } = string.Empty;

    /// <summary>
    /// What this member may do in the business (issue #521), stored as the constrained
    /// <see cref="BusinessRole"/> vocabulary rather than a free-text name or a set of flags.
    ///
    /// Required, and <see cref="BusinessRole.Owner"/> by default. The default is not a convenience:
    /// it is the same value the additive migration backfills every existing row with, so a
    /// membership created by the existing <c>bootstrap-business</c> path keeps exactly the access
    /// it has today. A creation path that means something else - the member management of issue
    /// #524 - states the role deliberately instead of relying on it.
    ///
    /// A stored value this code does not declare (a hand-written <c>UPDATE</c>, or a role a newer
    /// deployment wrote) is carried through to
    /// <see cref="BusinessMembershipResolutionPolicy"/> unchanged and denies access there. It is
    /// deliberately not corrected on read: silently reading it as some default role would grant
    /// access nobody recorded.
    /// </summary>
    public BusinessRole Role { get; set; } = BusinessRole.Owner;

    /// <summary>
    /// Revoking a membership sets this to false rather than deleting the row, so the revoked
    /// approval stays visible and access stops on the next request.
    ///
    /// This is a current-state flag, not an audit trail: the row records that the membership is
    /// now inactive and, since issue #522, when its state last changed - but not who changed it,
    /// and not the states it held before. A historical audit log of membership changes is still
    /// not part of this table.
    ///
    /// At most one row per <c>(DirectoryTenantId, ObjectId)</c> may carry <c>true</c>, enforced by
    /// the filtered unique index in <c>AppDbContext.ConfigureTenancy</c> (issue #522). Every write
    /// that creates, reactivates or deactivates a membership therefore goes through
    /// <c>Inventory.Application.Tenancy.MembershipWriteGuard</c>, which re-checks
    /// <see cref="MembershipEligibility"/> inside the write transaction the index backs up.
    /// </summary>
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>
    /// When this membership's state last changed (issue #522): set when the row is created, and
    /// again whenever it is revoked, reactivated or rejected.
    ///
    /// It exists so "the most recent record decides" is answerable for an identity with no active
    /// membership - the account-state precedence of #523, which must distinguish an old rejection
    /// from a newer revocation. <see cref="CreatedAtUtc"/> cannot answer that: a row revoked years
    /// after it was created would still look like the oldest state change.
    ///
    /// Required, and the additive migration filled every pre-existing row from
    /// <see cref="CreatedAtUtc"/>, which is the only instant those rows record. A write path that
    /// changes <see cref="IsActive"/> without setting this leaves the column describing a state
    /// the row no longer holds, so it is set in the same operation as the change rather than
    /// afterwards. A <see cref="Role"/> change is deliberately not one of those changes: it moves
    /// what a member may do, not whether the membership is current, and the account-state
    /// precedence this column serves is about the latter.
    /// </summary>
    public DateTime StatusChangedAtUtc { get; set; }
}
