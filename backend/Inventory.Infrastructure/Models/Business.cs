using System.Text.Json.Serialization;

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
    /// Deactivating a business denies access to its data without deleting the records, so an
    /// offboarded business fails closed rather than being dropped.
    /// </summary>
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAtUtc { get; set; }

    [JsonIgnore]
    public virtual ICollection<BusinessMembership> Memberships { get; set; } = new List<BusinessMembership>();
}
