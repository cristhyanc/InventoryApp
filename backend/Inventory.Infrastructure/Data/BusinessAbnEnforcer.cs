using Inventory.Domain.Exceptions;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Data;

/// <summary>
/// Refuses to store a <see cref="Business.Abn"/> that is not a well-formed Australian Business
/// Number (issue #540).
///
/// It is enforced centrally on <c>SaveChanges</c>, for the same reason the business time zone is
/// (<see cref="BusinessTimeZoneEnforcer"/>) and tenant ownership is
/// (<see cref="BusinessOwnershipEnforcer"/>): the ABN appears on tax invoices and in the GST
/// accounting aid, so "the write path remembered to validate it" is not a strong enough guarantee.
/// Whichever path writes a business - the self-service application, an Owner's settings change, or
/// a human-invoked command - a number that cannot be an ABN is rejected here rather than becoming
/// a wrong fact about the business.
///
/// The ABN is optional, so <see langword="null"/> is accepted and is the one representation of
/// "not recorded"; a blank or whitespace value is refused rather than treated as a second kind of
/// absence. A value carrying spaces is refused too: normalising on the way through a
/// <c>SaveChanges</c> hook would silently rewrite what a caller submitted, so turning
/// "51 824 753 556" into the stored digits belongs to the write path that accepted it
/// (<see cref="AustralianBusinessNumber.TryNormalize"/>).
///
/// The refusal is a <see cref="DomainValidationException"/> because the value is one a caller
/// submits: its message names only the rule, carries no internal detail, and the HTTP boundary
/// answers it with <c>400</c>.
/// </summary>
internal static class BusinessAbnEnforcer
{
    public static void Enforce(AppDbContext context)
    {
        foreach (var entry in context.ChangeTracker.Entries<Business>())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified))
            {
                continue;
            }

            var abn = entry.Entity.Abn;
            if (abn is not null && !AustralianBusinessNumber.IsValid(abn))
            {
                throw new DomainValidationException(
                    $"{AustralianBusinessNumber.InvalidMessage} Nothing was saved.");
            }
        }
    }
}
