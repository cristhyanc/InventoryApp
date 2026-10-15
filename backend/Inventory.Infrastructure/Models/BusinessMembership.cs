using System.Text.Json.Serialization;
using Inventory.Domain.Tenancy;

namespace Inventory.Infrastructure.Models;

/// <summary>
/// Approves one authenticated Microsoft Entra actor, identified by the validated
/// <c>(tid, oid)</c> claim pair, for one <see cref="Models.Business"/> (issue #64).
///
/// This table is the application's authorization boundary for data ownership: an actor with no
/// active row here reads no business data, whatever Entra directory issued a valid token. A row is
/// created either by the human-invoked <c>bootstrap-business</c> command or, since issue #532, by
/// redeeming an invite an Owner of that business created - never by self-service sign-up, and never
/// from anything a request chooses: a redemption takes the business from the invite row and the
/// identity from the caller's own validated token.
///
/// Since issue #524 it also stores two pieces of personal data, <see cref="DisplayName"/> and
/// <see cref="Email"/>, and they are <em>display only</em>: an Owner has to be able to see who
/// their members are, and an Entra object id does not tell them. Nothing else changed about this
/// table's role. Identity is still the <c>(tid, oid)</c> pair alone, because an email address and a
/// display name are mutable and reassignable and so must never decide ownership; neither column is
/// read by any authorization, membership, role or capability decision, neither is ever logged, and
/// neither is reachable through the platform-diagnostics surface, which excludes this table
/// entirely.
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
    /// The member's display name, as their validated sign-in token last reported it (issue #524),
    /// or <c>null</c> when no token has reported one.
    ///
    /// Display only. It exists so a members list can show a person rather than a GUID, and it is
    /// refreshed from the <c>name</c> claim at most once an hour by the dedicated write path that
    /// runs after a response is decided. Nullable because a membership can be created - by
    /// <c>bootstrap-business</c> - before its member has ever signed in, and because a token need
    /// not carry the claim at all.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// The member's email address, from the same token and on the same terms as
    /// <see cref="DisplayName"/> (issue #524): the <c>preferred_username</c> claim, or the
    /// <c>email</c> claim when that is what the token carries.
    ///
    /// It is a mailbox to display, never a credential and never an identifier. No lookup, no
    /// membership resolution and no invitation matching may key off it: two directories may issue
    /// the same address, an address can be reassigned to a different person, and the one thing that
    /// identifies an actor here is the <c>(tid, oid)</c> pair above.
    /// </summary>
    public string? Email { get; set; }

    /// <summary>
    /// What this member may do in the business (issue #521), stored as the constrained
    /// <see cref="BusinessRole"/> vocabulary rather than a free-text name or a set of flags.
    ///
    /// <c>required</c>, and with no default of any kind (issue #679). It used to default to
    /// <see cref="BusinessRole.Owner"/> - the value the additive migration backfilled every
    /// existing row with - which made the most privileged role in the business the one a creation
    /// path could get by saying nothing. Every path now states the role it means: the invite
    /// redemption the role on the invite, the self-service application <c>Owner</c>, and
    /// <c>bootstrap-business</c> the configured one. The compiler, not a convention, is what
    /// requires that.
    ///
    /// A stored value this code does not declare (a hand-written <c>UPDATE</c>, a role a newer
    /// deployment wrote, or the <c>0</c> an <c>INSERT</c> that names no role now gets from the
    /// column default) is carried through to <see cref="BusinessMembershipResolutionPolicy"/>
    /// unchanged and denies access there. It is deliberately not corrected on read: silently
    /// reading it as some default role would grant access nobody recorded.
    /// </summary>
    public required BusinessRole Role { get; set; }

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
    ///
    /// <c>required</c>, and with no default, on <see cref="Role"/>'s terms (issue #679): whether a
    /// membership grants anything at all is not a value a creation path should be able to leave
    /// unsaid. The database half needs no migration, because this column never had a default -
    /// <c>AddBusinessOwnershipModel</c> declared it <c>NOT NULL</c> with none - so an
    /// <c>INSERT</c> that omits it is rejected by SQLite rather than quietly made active.
    /// </summary>
    public required bool IsActive { get; set; }

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
