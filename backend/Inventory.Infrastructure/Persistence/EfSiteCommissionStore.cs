using Inventory.Application.Commissions;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using SiteCommissionAgreementEntity = Inventory.Infrastructure.Models.SiteCommissionAgreement;

namespace Inventory.Infrastructure.Persistence;

public sealed class EfSiteCommissionStore : ISiteCommissionStore
{
    private readonly AppDbContext _db;

    public EfSiteCommissionStore(AppDbContext db)
    {
        _db = db;
    }

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
