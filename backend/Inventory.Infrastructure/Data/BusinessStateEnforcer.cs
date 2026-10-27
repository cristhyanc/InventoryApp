using Inventory.Domain.Exceptions;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Data;

/// <summary>
/// Refuses to store a <see cref="Business.State"/> that is not one of the eight Australian states
/// and territories (issue #933).
///
/// It is enforced centrally on <c>SaveChanges</c>, for the same reason the ABN is
/// (<see cref="BusinessAbnEnforcer"/>) and the time zone is
/// (<see cref="BusinessTimeZoneEnforcer"/>): the state decides which supplier records a business is
/// offered (#817), so "the write path remembered to validate it" is not a strong enough guarantee.
/// Whichever path writes a business - the self-service application, an Owner's settings change, or a
/// human-invoked command - a value outside <see cref="AustralianStates.All"/> is rejected here
/// rather than becoming a wrong fact about the business.
///
/// The state is nullable in the stored shape, so <see langword="null"/> is accepted and is the one
/// representation of "not recorded": every business that predates the column has none, and nothing
/// backfills them. A blank or whitespace value is refused rather than treated as a second kind of
/// absence, and a value that merely differs in case or spacing is refused too: normalising inside a
/// <c>SaveChanges</c> hook would silently rewrite what a caller submitted, so turning " nsw " into
/// the stored abbreviation belongs to the write path that accepted it
/// (<see cref="AustralianStates.TryNormalize"/>).
///
/// The refusal is a <see cref="DomainValidationException"/> because the value is one a caller
/// submits: its message names only the rule and the eight values, carries no internal detail, and
/// the HTTP boundary answers it with <c>400</c>.
/// </summary>
internal static class BusinessStateEnforcer
{
    public static void Enforce(AppDbContext context)
    {
        foreach (var entry in context.ChangeTracker.Entries<Business>())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified))
            {
                continue;
            }

            var state = entry.Entity.State;
            if (state is not null && !AustralianStates.IsValid(state))
            {
                throw new DomainValidationException($"{AustralianStates.InvalidMessage} Nothing was saved.");
            }
        }
    }
}
