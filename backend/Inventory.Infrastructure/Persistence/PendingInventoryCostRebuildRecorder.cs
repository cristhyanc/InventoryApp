using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The one way a <see cref="PendingInventoryCostRebuild"/> is recorded (issues #1103 and #1105),
/// shared by the sales sync, the provider evidence write and an Owner decision so that every writer
/// enqueues work under the same rules. The rows it stages are saved by the caller, inside the
/// caller's own <c>BEGIN IMMEDIATE</c> transaction, beside the change that made the work necessary.
///
/// <para><strong>The instant only ever moves earlier.</strong> A product that already has a pending
/// row keeps it and has its instant lowered when this change's is earlier, because a replay from the
/// earlier instant covers the later one and not the other way round. Moving it later would silently
/// skip a sale's effect on cost. The unique index on <c>(BusinessId, ProductId)</c> is the database's
/// backstop under this read.</para>
///
/// <para><strong>The revision moves on every enqueue, including one the instant does not
/// notice.</strong> That is exactly why it exists: a change at or after the instant the row already
/// holds adds work without changing any other column, so without the revision a replay already in
/// flight - one that has read its costing inputs and not yet acknowledged - would find its claim
/// intact, save figures computed without that change and delete the work recorded for it.</para>
///
/// <para>The read is answered by the database rather than by a row this context tracked earlier,
/// and it sits inside the caller's write-locked transaction, so the revision this increments from is
/// the stored one and no other writer can commit between the read and the save.</para>
/// </summary>
internal static class PendingInventoryCostRebuildRecorder
{
    /// <summary>
    /// The revision a <see cref="PendingInventoryCostRebuild"/> is first recorded at. Every later
    /// enqueue for the same product moves it on by one, which is what an acknowledgement compares.
    /// </summary>
    public const long FirstRevision = 1;

    /// <summary>
    /// Stages one pending rebuild per product in <paramref name="affected"/>, from that product's
    /// earliest affected instant. Saves nothing.
    /// </summary>
    public static async Task StageAsync(
        AppDbContext db,
        IReadOnlyDictionary<long, DateTime> affected,
        DateTime createdAtUtc,
        CancellationToken cancellationToken)
    {
        if (affected.Count == 0)
            return;

        ForgetTracked(db);

        var productIds = affected.Keys.ToList();
        var existing = await db.PendingInventoryCostRebuilds
            .Where(row => productIds.Contains(row.ProductId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var (productId, fromInstantUtc) in affected)
        {
            var row = existing.Find(candidate => candidate.ProductId == productId);
            if (row is null)
            {
                db.PendingInventoryCostRebuilds.Add(new PendingInventoryCostRebuild
                {
                    ProductId = productId,
                    FromInstantUtc = fromInstantUtc,
                    CreatedAtUtc = createdAtUtc,
                    Revision = FirstRevision,

                    // BusinessId is deliberately left unset: BusinessOwnershipEnforcer stamps it from
                    // the resolved scope on SaveChanges, exactly as it does for every other
                    // tenant-owned insert, so this code never assigns an owner itself.
                });
                continue;
            }

            // Before the instant, and whatever the instant does: this change is work the row did not
            // describe a moment ago, and saying so is the only way a replay in flight can tell.
            row.Revision++;

            if (fromInstantUtc < row.FromInstantUtc)
                row.FromInstantUtc = fromInstantUtc;
        }
    }

    /// <summary>
    /// Adds <paramref name="productId"/> at <paramref name="instantUtc"/> to
    /// <paramref name="affected"/>, keeping the earlier instant when the product is already there.
    /// </summary>
    public static void Add(Dictionary<long, DateTime> affected, long? productId, DateTime? instantUtc)
    {
        if (productId is not { } id || instantUtc is not { } at)
            return;

        if (!affected.TryGetValue(id, out var existingAt) || at < existingAt)
            affected[id] = at;
    }

    /// <summary>
    /// Detaches every <see cref="PendingInventoryCostRebuild"/> the context is tracking, so the read
    /// that follows is answered by the database rather than by an instance read before another writer
    /// moved the row. Every caller stages and saves a pending row within its own write-locked call, so
    /// the context never holds an unsaved pending row at this point. Nothing else tracked is touched.
    /// </summary>
    public static void ForgetTracked(AppDbContext db)
    {
        foreach (var tracked in db.ChangeTracker.Entries<PendingInventoryCostRebuild>().ToList())
            tracked.State = EntityState.Detached;
    }
}
