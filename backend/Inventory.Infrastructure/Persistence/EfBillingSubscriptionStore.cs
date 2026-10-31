using Inventory.Application.Subscriptions;
using Inventory.Domain.Subscriptions;
using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IBillingSubscriptionStore"/> (issue #966). Like every
/// other adapter in this folder it lives beside the <see cref="AppDbContext"/> and the persistence
/// model it reads.
///
/// <para><strong>One table, one row, read-only.</strong> It touches
/// <c>BusinessBillingSubscriptions</c> and nothing else, and offers no write at all: the record is
/// written exclusively by the verified billing events of #969. There is no
/// <c>IgnoreQueryFilters</c>, no raw SQL and no business-id parameter - the row comes through the
/// central tenant query filter on the request's own resolved scope, so the subscription reported is
/// the business the request is acting in and no caller-supplied value can choose another one.</para>
///
/// <para><strong>It interprets nothing.</strong> The stored plan, status and period are reported as
/// they are, including values this code does not declare; whether any of them entitles anything is
/// <see cref="EffectiveEntitlementPolicy"/>'s single decision.</para>
/// </summary>
public sealed class EfBillingSubscriptionStore : IBillingSubscriptionStore
{
    private readonly AppDbContext _db;

    public EfBillingSubscriptionStore(AppDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task<BillingSubscriptionState?> FindForCurrentBusinessAsync(
        CancellationToken cancellationToken)
    {
        var stored = await _db.BusinessBillingSubscriptions
            .AsNoTracking()
            .Select(row => new
            {
                row.Plan,
                row.Status,
                row.CurrentPeriodStartUtc,
                row.CurrentPeriodEndUtc,
            })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return stored is null
            ? null
            : new BillingSubscriptionState(
                stored.Plan,
                stored.Status,
                new EntitlementPeriod(stored.CurrentPeriodStartUtc, stored.CurrentPeriodEndUtc));
    }
}
