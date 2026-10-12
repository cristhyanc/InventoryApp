using System.Text.Json.Serialization;
using Inventory.Domain.Tenancy;

namespace Inventory.Infrastructure.Models;

/// <summary>
/// An application-owned business: the data-ownership boundary for this vending operation
/// (issue #64). Its <see cref="Id"/> is the tenant key the rest of the application scopes by.
///
/// It is not a Microsoft Entra directory tenant. Actors from an Entra directory are mapped onto
/// a business through explicit <see cref="BusinessMembership"/> rows, so the identity provider
/// never decides data ownership on its own.
/// </summary>
public class Business
{
    public int Id { get; set; }

    /// <summary>
    /// Operator-facing display name. It is not an identifier: nothing resolves a business by
    /// name, it is not unique, and it may be changed without affecting data ownership.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The IANA time-zone identifier the business's calendar days are derived in, for example
    /// <c>Australia/Sydney</c> or <c>America/New_York</c> (issue #499). It is what turns a stored
    /// UTC instant into the business date a report, a dashboard period or an effective-dated
    /// lookup means, through <c>Inventory.Application.Time.IBusinessCalendar</c>.
    ///
    /// Required and validated: a value that the host's time-zone database cannot resolve is
    /// refused by <see cref="Data.BusinessTimeZoneEnforcer"/> on <c>SaveChanges</c> rather than
    /// stored, because an unresolvable zone would leave every business date underivable. The
    /// default is the zone the application's single existing business has always used and the
    /// value the additive migration backfills; a newly onboarded business is given its own zone
    /// explicitly.
    /// </summary>
    public string TimeZoneId { get; set; } = "Australia/Sydney";

    /// <summary>
    /// Where the business is in its lifecycle (issue #540), and the single source of truth for
    /// whether its data may be reached: only <see cref="BusinessStatus.Active"/> resolves, and
    /// every other value - a pending application, a rejected one, an offboarded business, or a
    /// value this code does not declare - denies access in
    /// <see cref="BusinessMembershipResolutionPolicy"/> with the existing denial. It replaced the
    /// former <c>IsActive</c> flag, which could not tell those situations apart, and no other
    /// column mirrors it.
    ///
    /// <c>required</c>, and with no default at all (issue #584). It used to default to
    /// <see cref="BusinessStatus.Active"/>, which made the status the one thing about a new business
    /// that could be decided by forgetting to decide it: a write path that never assigned this
    /// property created a trading tenant whose data is reachable. Every path therefore states it -
    /// <c>bootstrap-business</c> states <see cref="BusinessStatus.Active"/> because it adopts a
    /// business that is already trading, and a self-service application states
    /// <see cref="BusinessStatus.Pending"/> - and the compiler, not a convention, is what requires
    /// that. The value an instance holds before anything states one is <c>0</c>, which
    /// <see cref="BusinessStatuses"/> declares no status at and
    /// <see cref="BusinessMembershipResolutionPolicy"/> denies, and which
    /// <see cref="Data.BusinessStatusEnforcer"/> refuses to store on <c>SaveChanges</c>.
    ///
    /// Deactivating or rejecting a business denies access to its data without deleting the
    /// records, so an offboarded or refused business fails closed rather than being dropped.
    /// </summary>
    public required BusinessStatus Status { get; set; }

    /// <summary>
    /// The business's own contact email address, as its application recorded it (issue #540), and
    /// <c>null</c> when none was recorded.
    ///
    /// Nullable because the application's existing business predates the application form and has
    /// no such value: the migration invents none rather than copying a member's address, which
    /// would be a different fact about a different party. It is business contact detail, never an
    /// identity - no sign-in, membership, invitation or lookup may key off it, for the same
    /// reasons that apply to <see cref="BusinessMembership.Email"/>.
    /// </summary>
    public string? ContactEmail { get; set; }

    /// <summary>
    /// Whether the business is registered for GST, or <c>null</c> when that has not been recorded
    /// (issue #540).
    ///
    /// Three states, deliberately: "registered", "not registered" and "we do not know" are
    /// different facts, and the existing business migrates as unknown rather than being assumed to
    /// be registered. GST registration is never inferred from anything else - not from an
    /// <see cref="Abn"/>, which an unregistered entity may hold, and not from GST appearing on a
    /// supplier's invoice. What a non-registered business's GST report does with this is a
    /// deliberate follow-up, so nothing reads it yet.
    /// </summary>
    public bool? GstRegistered { get; set; }

    /// <summary>
    /// The business's Australian Business Number as 11 digits with no spaces, or <c>null</c> when
    /// none was recorded (issue #540).
    ///
    /// Optional, and checksum-validated wherever it is set:
    /// <see cref="Data.BusinessAbnEnforcer"/> refuses a value that is not a well-formed ABN on
    /// <c>SaveChanges</c>, by the rule in <see cref="AustralianBusinessNumber"/>. The check proves
    /// the digits are well-formed and never that the ABN is registered, current or this
    /// business's. "Not recorded" has exactly one representation - <c>null</c> - so a blank string
    /// is refused rather than stored as a second kind of absence.
    /// </summary>
    public string? Abn { get; set; }

    /// <summary>
    /// When the business applied, and <c>null</c> for a business that never went through an
    /// application - the existing one, and any created by <c>bootstrap-business</c> (issue #540).
    /// A missing instant is not backfilled from <see cref="CreatedAtUtc"/>: the record was created
    /// by an operator adopting existing data, which is not an application, and claiming otherwise
    /// would invent a business event.
    /// </summary>
    public DateTime? AppliedAtUtc { get; set; }

    /// <summary>
    /// When the application was approved or refused, and <c>null</c> while no decision has been
    /// recorded (issue #540). Approve, reject and deactivate are issue #509's work; nothing writes
    /// this yet.
    /// </summary>
    public DateTime? DecidedAtUtc { get; set; }

    /// <summary>
    /// Why the application was refused, recorded by the operator who refused it, and <c>null</c>
    /// otherwise (issue #540).
    ///
    /// It is the one piece of a <see cref="BusinessStatus.Rejected"/> business reported outside
    /// it: <c>GET /api/me/account-state</c> returns it to the applicant's own identity alongside
    /// <see cref="AccountState.ApplicationRejected"/>, and to nobody else. A rejection with no
    /// recorded reason is still a rejection - the state never silently becomes a revocation
    /// because the text is missing.
    /// </summary>
    public string? RejectionReason { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    [JsonIgnore]
    public virtual ICollection<BusinessMembership> Memberships { get; set; } = new List<BusinessMembership>();
}
