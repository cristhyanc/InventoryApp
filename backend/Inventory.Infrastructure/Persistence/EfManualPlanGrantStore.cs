using Inventory.Application.Subscriptions;
using Inventory.Domain.Subscriptions;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IManualPlanGrantStore"/> (issue #966).
///
/// <para><strong>One table, and one write.</strong> It touches <c>BusinessPlanGrants</c> and nothing
/// else. The read lists the current business's grants through the central tenant query filter; the
/// write adds the grandfathered grant through the central ownership stamp, so the business key comes
/// from the scope the caller resolved and never from a parameter. There is no
/// <c>IgnoreQueryFilters</c>, no raw SQL and no business-id argument anywhere on either path.</para>
///
/// <para><strong>The write is one transaction, and the database is the backstop.</strong> The check
/// for an existing grandfathered grant and the insert run inside one <c>BEGIN IMMEDIATE</c>
/// transaction, so the lock is taken before the read the decision is made on rather than at the
/// insert: two runs of the command cannot both find none. Underneath that, the filtered unique index
/// on <c>(BusinessId, Source)</c> refuses a second grandfathered row outright, which is what makes
/// "safe to rerun" a property of the schema and not only of the code. A business that already holds
/// one is left exactly as it is - nothing is rewritten, so an end date an owner has since set
/// survives a rerun untouched.</para>
/// </summary>
public sealed class EfManualPlanGrantStore : IManualPlanGrantStore
{
    private readonly AppDbContext _db;

    public EfManualPlanGrantStore(AppDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ManualPlanGrantState>> ListForCurrentBusinessAsync(
        CancellationToken cancellationToken)
    {
        var stored = await _db.BusinessPlanGrants
            .AsNoTracking()
            .OrderBy(row => row.Id)
            .Select(row => new
            {
                row.Plan,
                row.Source,
                row.StartsAtUtc,
                row.EndsAtUtc,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Reported as stored, every row of it: which grant applies at an instant is the Domain
        // policy's decision, and a store that filtered by date here would be a second place the
        // half-open period rule lived.
        return stored.ConvertAll(row => new ManualPlanGrantState(
            row.Plan,
            row.Source,
            new EntitlementPeriod(row.StartsAtUtc, row.EndsAtUtc)));
    }

    /// <inheritdoc />
    public async Task<bool> GrantGrandfatheredPlanIfMissingAsync(
        DateTime startsAtUtc,
        DateTime grantedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var transaction = await SqliteWriteLockTransaction
            .BeginAsync(_db, cancellationToken)
            .ConfigureAwait(false);

        var alreadyGranted = await _db.BusinessPlanGrants
            .AsNoTracking()
            .AnyAsync(
                row => row.Source == ManualPlanGrantSource.Grandfathered,
                cancellationToken)
            .ConfigureAwait(false);

        if (alreadyGranted)
        {
            // A rerun. Nothing is written at all - not the start, not the plan, and not an end date
            // an owner may have set since - because a rerun that changed a grant would not be a
            // rerun.
            return false;
        }

        _db.BusinessPlanGrants.Add(new BusinessPlanGrant
        {
            Plan = GrandfatheringEligibilityPolicy.GrandfatheredPlan,
            Source = ManualPlanGrantSource.Grandfathered,
            StartsAtUtc = startsAtUtc,
            EndsAtUtc = null,
            Reason = null,
            GrantedAtUtc = grantedAtUtc,
        });

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return true;
    }
}
