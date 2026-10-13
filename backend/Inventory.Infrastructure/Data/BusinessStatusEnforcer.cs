using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Data;

/// <summary>
/// Refuses to store a <see cref="Business.Status"/> that is not a declared
/// <see cref="BusinessStatus"/> (issue #584).
///
/// It is enforced centrally on <c>SaveChanges</c>, beside <see cref="BusinessTimeZoneEnforcer"/> and
/// <see cref="BusinessAbnEnforcer"/> and for the same reason: the status is the single thing that
/// decides whether a business's financial data may be reached, so "the write path remembered to set
/// it" is not a strong enough guarantee. <c>required</c> on the property makes an omitted status a
/// compile error; this is what covers the ways a value can still arrive undeclared at runtime - a
/// cast integer, a row an out-of-band <c>INSERT</c> left at the column's default and a later save
/// would carry forward.
///
/// The refusal is an <see cref="InvalidOperationException"/> rather than a
/// <c>DomainValidationException</c>, because nothing a caller submits can reach here: no route,
/// query, body or header names a business status, and the self-service application and the bootstrap
/// both choose theirs in code. Reaching this is a programming error, so the HTTP boundary answers it
/// with its generic <c>500</c> rather than with a <c>400</c> that would tell a caller they got
/// something wrong. The message names the rule and the stored value, which is a status rather than
/// anyone's data, so it is safe to log.
/// </summary>
internal static class BusinessStatusEnforcer
{
    public static void Enforce(AppDbContext context)
    {
        foreach (var entry in context.ChangeTracker.Entries<Business>())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified))
            {
                continue;
            }

            var status = entry.Entity.Status;
            if (!BusinessStatuses.IsSupported(status))
            {
                throw new InvalidOperationException(
                    $"A business cannot be stored with the undeclared lifecycle status {(int)status}. "
                        + BusinessStatuses.UnsupportedMessage
                        + " Nothing was saved.");
            }
        }
    }
}
