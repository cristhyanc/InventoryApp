using Inventory.Application.Businesses;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IBusinessSettingsStore"/> (issue #548). Like every other
/// adapter in this folder it lives in Inventory.Infrastructure beside the <see cref="AppDbContext"/>
/// and the persistence models it depends on.
///
/// <para><c>Businesses</c> is one of the deliberately global tables - it is the owner, not owned -
/// so both the read and the write are by primary key, and the key is only ever one tenancy
/// resolution produced from the authenticated actor's membership. That is what keeps this write,
/// which no query filter scopes, confined to the caller's own business: there is no predicate here
/// a request could influence, and no other business's row is loaded, let alone changed.</para>
///
/// <para><strong>The write goes through <c>SaveChanges</c> on a tracked entity, deliberately.</strong>
/// It is not an <c>ExecuteUpdate</c>: the central enforcers that validate a business row -
/// <see cref="BusinessTimeZoneEnforcer"/> for the time zone and <see cref="BusinessAbnEnforcer"/>
/// for the ABN - run on <c>SaveChanges</c>, and a set-based update would write straight past both.
/// The row is read, compared and written inside one transaction, so the change set this reports is
/// evidence about the state the write replaced rather than about a state another writer may already
/// have changed.</para>
/// </summary>
public sealed class EfBusinessSettingsStore : IBusinessSettingsStore
{
    private readonly AppDbContext _db;

    public EfBusinessSettingsStore(AppDbContext db)
    {
        _db = db;
    }

    public async Task<BusinessSettings?> FindAsync(BusinessId businessId, CancellationToken cancellationToken) =>
        await _db.Businesses
            .AsNoTracking()
            .Where(business => business.Id == businessId.Value)
            .Select(business => new BusinessSettings(
                business.Name,
                business.ContactEmail,
                business.Abn,
                business.GstRegistered,
                business.TimeZoneId,
                business.State))
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<BusinessSettingsSaveResult?> SaveAsync(
        BusinessId businessId,
        BusinessSettings submitted,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submitted);

        // A caller that already opened a transaction on this context owns the commit, and this save
        // joins it rather than starting a second one - which SQLite would refuse. With no ambient
        // transaction the read and the write get one of their own, which is what makes the reported
        // change set describe the row this write actually replaced.
        await using var transaction = _db.Database.IsRelational() && _db.Database.CurrentTransaction is null
            ? await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;

        var business = await _db.Businesses
            .SingleOrDefaultAsync(candidate => candidate.Id == businessId.Value, cancellationToken)
            .ConfigureAwait(false);

        if (business is null)
        {
            return null;
        }

        var current = new BusinessSettings(
            business.Name,
            business.ContactEmail,
            business.Abn,
            business.GstRegistered,
            business.TimeZoneId,
            business.State);
        var changed = BusinessSettingsPolicy.ChangedFields(current, submitted);

        business.Name = submitted.Name;
        business.ContactEmail = submitted.ContactEmail;
        business.Abn = submitted.Abn;
        business.GstRegistered = submitted.GstRegistered;
        business.TimeZoneId = submitted.TimeZoneId;
        business.State = submitted.State;

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return new BusinessSettingsSaveResult(submitted, changed);
    }
}
