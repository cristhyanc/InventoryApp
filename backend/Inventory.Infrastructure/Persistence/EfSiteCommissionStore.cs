using Inventory.Application.Commissions;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using SiteCommissionAgreementEntity = Inventory.Infrastructure.Models.SiteCommissionAgreement;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="ISiteCommissionStore"/>.
///
/// <para>Both agreement writes run inside <see cref="SqliteWriteLockTransaction"/>
/// (<c>BEGIN IMMEDIATE</c>), opened by the use case before the read that decides the write
/// (issue #1254). <see cref="AddAgreementAsync"/> and <see cref="SetAgreementEndDateAsync"/>
/// therefore save inside that transaction and do not commit it.</para>
///
/// <para>Every statement here is an ordinary filtered tenant-owned read or write: the agreement a
/// route value names is found only while the caller's own business holds it, through the central
/// query filter rather than a <c>Where</c> clause of this adapter's own.</para>
/// </summary>
public sealed class EfSiteCommissionStore : ISiteCommissionStore
{
    private readonly AppDbContext _db;

    public EfSiteCommissionStore(AppDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task<ISiteCommissionWriteTransaction> BeginWriteTransactionAsync(CancellationToken cancellationToken) =>
        new WriteTransaction(await SqliteWriteLockTransaction.BeginAsync(_db, cancellationToken).ConfigureAwait(false));

    public async Task<IReadOnlyList<SiteCommissionSaleFact>> GetCompletedSalesAsync(
        IReadOnlyCollection<long> machineIds,
        DateTime from,
        DateTime endExclusive,
        CancellationToken cancellationToken) =>
        await _db.NayaxSales.AsNoTracking()
            .Where(sale => machineIds.Contains(sale.MachineID) &&
                sale.MachineAuthorizationTime >= from &&
                sale.MachineAuthorizationTime < endExclusive)
            .Where(EfNayaxSalesQueries.CompletedSalePredicate)
            .Select(sale => new SiteCommissionSaleFact(
                sale.MachineID,
                sale.MachineAuthorizationTime,
                sale.SettlementValue,
                sale.PaymentMethod,
                sale.ProductName))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<CommissionAgreement>> GetAgreementsAsync(
        long? siteId,
        CancellationToken cancellationToken) =>
        (await _db.SiteCommissionAgreements.AsNoTracking()
            .Where(agreement => !siteId.HasValue || agreement.SiteId == siteId.Value)
            .OrderBy(agreement => agreement.EffectiveFrom)
            .ToListAsync(cancellationToken))
        .Select(ToDomain)
        .ToList();

    public Task<bool> HasOverlappingAgreementAsync(
        long siteId,
        DateTime effectiveFrom,
        DateTime? effectiveTo,
        CancellationToken cancellationToken) =>
        _db.SiteCommissionAgreements.AnyAsync(agreement =>
            agreement.SiteId == siteId &&
            agreement.EffectiveFrom <= (effectiveTo ?? DateTime.MaxValue) &&
            (agreement.EffectiveTo ?? DateTime.MaxValue) >= effectiveFrom,
            cancellationToken);

    public async Task<CommissionAgreement?> FindAgreementAsync(
        int agreementId,
        CancellationToken cancellationToken)
    {
        var entity = await _db.SiteCommissionAgreements.AsNoTracking()
            .FirstOrDefaultAsync(agreement => agreement.Id == agreementId, cancellationToken);
        return entity is null ? null : ToDomain(entity);
    }

    public async Task<CommissionAgreement> SetAgreementEndDateAsync(
        int agreementId,
        DateTime endDate,
        DateTime occurredAtUtc,
        CancellationToken cancellationToken)
    {
        // Tracked, because this is the write: the row is loaded through the same tenant filter the
        // lookup above uses, so an agreement of another business is not here to be updated.
        var entity = await _db.SiteCommissionAgreements
            .FirstAsync(agreement => agreement.Id == agreementId, cancellationToken);

        entity.EffectiveTo = endDate;
        entity.UpdatedAt = occurredAtUtc;
        await _db.SaveChangesAsync(cancellationToken);
        return ToDomain(entity);
    }

    public async Task<CommissionAgreement> AddAgreementAsync(
        CommissionAgreement agreement,
        CancellationToken cancellationToken)
    {
        var entity = new SiteCommissionAgreementEntity
        {
            SiteId = agreement.SiteId,
            EffectiveFrom = agreement.EffectiveFrom,
            EffectiveTo = agreement.EffectiveTo,
            CommissionRate = agreement.CommissionRate,
            Frequency = agreement.Frequency,
            Basis = agreement.Basis,
            PaymentDueDaysAfterPeriodEnd = agreement.PaymentDueDaysAfterPeriodEnd
        };
        _db.SiteCommissionAgreements.Add(entity);
        await _db.SaveChangesAsync(cancellationToken);
        return ToDomain(entity);
    }

    /// <summary>
    /// The Application-facing handle over <see cref="SqliteWriteLockTransaction"/>, so the use case
    /// owns the commit without Application referencing Infrastructure.
    /// </summary>
    private sealed class WriteTransaction : ISiteCommissionWriteTransaction
    {
        private readonly SqliteWriteLockTransaction _transaction;

        public WriteTransaction(SqliteWriteLockTransaction transaction) => _transaction = transaction;

        public Task CommitAsync(CancellationToken cancellationToken) =>
            _transaction.CommitAsync(cancellationToken);

        public ValueTask DisposeAsync() => _transaction.DisposeAsync();
    }

    private static CommissionAgreement ToDomain(SiteCommissionAgreementEntity entity) =>
        new(
            entity.Id,
            entity.SiteId,
            entity.EffectiveFrom,
            entity.EffectiveTo,
            entity.CommissionRate,
            entity.Frequency,
            entity.Basis,
            entity.PaymentDueDaysAfterPeriodEnd,
            entity.CreatedAt,
            entity.UpdatedAt);
}
