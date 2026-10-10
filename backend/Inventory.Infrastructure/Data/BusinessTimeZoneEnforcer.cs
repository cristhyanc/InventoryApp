using Inventory.Domain.Exceptions;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Time;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Data;

/// <summary>
/// Refuses to store a <see cref="Business.TimeZoneId"/> the host's time-zone database cannot
/// resolve (issue #499).
///
/// It is enforced centrally on <c>SaveChanges</c>, for the same reason tenant ownership is
/// (<see cref="BusinessOwnershipEnforcer"/>): the zone is what every business date is derived
/// from, so "the write path remembered to validate it" is not a strong enough guarantee. Whichever
/// path writes a business - the human-invoked bootstrap, onboarding, or a later settings change -
/// an unresolvable or blank zone is rejected here before it reaches the database, rather than
/// surfacing later as a business whose reports cannot be dated at all.
///
/// Validation is deliberately the same <see cref="BusinessTimeZones"/> resolution the read path
/// uses, so an id that is stored is an id a calendar can be built from, on a Linux and on a
/// Windows host alike. The refusal is a <see cref="DomainValidationException"/> because the value
/// is one a caller may eventually submit (the Owner business-settings change of issue #511): its
/// message names only the submitted id, carries no internal detail, and the HTTP boundary answers
/// it with <c>400</c>.
/// </summary>
internal static class BusinessTimeZoneEnforcer
{
    public static void Enforce(AppDbContext context)
    {
        foreach (var entry in context.ChangeTracker.Entries<Business>())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified))
            {
                continue;
            }

            var timeZoneId = entry.Entity.TimeZoneId;
            if (!BusinessTimeZones.TryResolve(timeZoneId, out _))
            {
                throw new DomainValidationException(
                    $"'{timeZoneId}' is not a time zone this business can keep its calendar in. A "
                        + "business time zone must be an IANA identifier the host's time-zone "
                        + "database resolves, for example 'Australia/Sydney'. Nothing was saved.");
            }
        }
    }
}
