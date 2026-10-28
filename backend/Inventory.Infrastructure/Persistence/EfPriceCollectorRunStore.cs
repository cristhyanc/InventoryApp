using Inventory.Application.PriceCatalogue;
using Inventory.Domain.PriceCatalogue;
using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IPriceCollectorRunStore"/> (issue #924, parent #817):
/// the one write the collector work list makes.
///
/// <para><strong>Two platform tables and nothing else.</strong> <c>PriceCollectorRuns</c> and
/// <c>PriceCollectorWorkItems</c> carry no business key, so there is no filter to ignore and no stamp
/// to apply: a collector request's <em>denied</em> <c>BusinessScope</c> leaves
/// <c>BusinessOwnershipEnforcer</c> refusing every tenant-owned write exactly as it does for the
/// platform-admin surfaces, and nothing here tries one. There is no <c>IgnoreQueryFilters</c> in this
/// adapter, no raw SQL and no unscoped context.</para>
///
/// <para><strong>Expiring the open run and issuing the new one are one transaction.</strong> Without
/// it there would be an instant with two open runs, or - worse - one where the expiry committed and
/// the new run did not, leaving no open run at all while a collector was being told it had one. "At
/// most one open run" is the invariant the results endpoint of #937 reads, so it is the database's
/// rather than a sequence of saves'.</para>
///
/// <para><strong>Expiring removes nothing.</strong> An expired run keeps its counts, its work items
/// and their outcomes: what changes is its status and its end instant, which is the instant it was
/// expired rather than the instant it was due to expire - the two are different facts, and recording
/// the second as the first would say the run ended at a time nothing happened.</para>
/// </summary>
public sealed class EfPriceCollectorRunStore : IPriceCollectorRunStore
{
    private readonly AppDbContext _db;

    public EfPriceCollectorRunStore(AppDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task<IssuedPriceCollectorRun> IssueAsync(
        NewPriceCollectorRun run,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        foreach (var item in run.WorkItems)
        {
            // Refused before anything is written, and refused by the Domain rather than here: a work
            // item naming two products or none is work no result could ever be attributed to.
            PriceCollectorWorkItemPolicy.RequireConsistentWorkItem(
                item.MasterProductId,
                item.WaitingKey,
                outcome: null);
        }

        PriceCollectorRunPolicy.RequireConsistentRun(
            PriceCollectorRunStatus.Running,
            run.IssuedAtUtc,
            completedAtUtc: null,
            listingsCheckedCount: 0,
            observationsRecordedCount: 0,
            failureCount: 0);

        await using var transaction = await _db.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        var stillOpen = await _db.PriceCollectorRuns
            .Where(existing => existing.Status == PriceCollectorRunStatus.Running)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var open in stillOpen)
        {
            open.Status = PriceCollectorRunStatus.Expired;
            open.CompletedAtUtc = run.IssuedAtUtc;
        }

        var issued = new Models.PriceCollectorRun
        {
            StartedAtUtc = run.IssuedAtUtc,
            ExpiresAtUtc = run.ExpiresAtUtc,
            Status = PriceCollectorRunStatus.Running,
        };

        _db.PriceCollectorRuns.Add(issued);

        // Saved before the work items so the run has the key they reference. Both saves are inside
        // the one transaction, so a failure between them stores neither.
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var workItems = run.WorkItems
            .Select(item => new Models.PriceCollectorWorkItem
            {
                PriceCollectorRunId = issued.Id,
                SupplierPresetId = item.SupplierPresetId,
                MasterProductId = item.MasterProductId,
                WaitingKey = item.WaitingKey,
                IssuedAtUtc = run.IssuedAtUtc,
                Outcome = null,
                OutcomeAtUtc = null,
            })
            .ToList();

        _db.PriceCollectorWorkItems.AddRange(workItems);

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new IssuedPriceCollectorRun(
            issued.Id,
            issued.StartedAtUtc,
            run.ExpiresAtUtc,
            workItems.ConvertAll(item => new IssuedPriceCollectorWorkItem(
                item.Id,
                item.SupplierPresetId,
                item.MasterProductId,
                item.WaitingKey)));
    }
}
