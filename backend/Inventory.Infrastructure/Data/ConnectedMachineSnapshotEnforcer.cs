using System.Globalization;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Data;

/// <summary>
/// Refuses to change or remove a recorded connected-machine snapshot (issue #978).
///
/// <para>It is enforced centrally on <c>SaveChanges</c>, beside <see cref="BusinessOwnershipEnforcer"/>
/// and the business enforcers, for the reason those are: "snapshots are append-only billing
/// evidence" has to be a property of the persistence boundary rather than of whichever adapter
/// remembers it. The billing of #969 charges from these rows, so a count that could change after it
/// was charged would not be evidence - and the one safe way to keep that true is that no code path
/// can write one, not that no code path does today.</para>
///
/// <para>An insert is the only thing this table accepts, and a correction is a <em>new</em> row for a
/// later day: there is no update, delete or reversal path anywhere in the application, and the
/// Application port deliberately offers none.</para>
///
/// <para>The refusal is an <see cref="InvalidOperationException"/> rather than a
/// <c>DomainValidationException</c>, for the same reason the sales-provider enforcer's is: nothing a
/// caller submits can reach here, because no endpoint writes a snapshot and no request names one.
/// Reaching it is a programming error, so the HTTP boundary answers it with its generic <c>500</c>.
/// The message names the business and the day, which are not anybody's credentials.</para>
/// </summary>
internal static class ConnectedMachineSnapshotEnforcer
{
    public static void Enforce(AppDbContext context)
    {
        foreach (var entry in context.ChangeTracker.Entries<BusinessConnectedMachineSnapshot>())
        {
            if (entry.State is not (EntityState.Modified or EntityState.Deleted))
            {
                continue;
            }

            var removal = entry.State == EntityState.Deleted;

            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"A connected-machine snapshot is append-only billing evidence and cannot be "
                        + $"{(removal ? "deleted" : "changed")}: the save would "
                        + $"{(removal ? "remove" : "rewrite")} the snapshot of business "
                        + $"{entry.Entity.BusinessId} for "
                        + $"{entry.Entity.SnapshotDateUtc:yyyy-MM-dd} (account series "
                        + $"{entry.Entity.AccountSeries}). A later count is a later row. Nothing "
                        + $"was saved."));
        }
    }
}
