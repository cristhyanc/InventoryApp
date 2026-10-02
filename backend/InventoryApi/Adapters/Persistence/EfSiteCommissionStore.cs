using Inventory.Application.Commissions;
using Inventory.Domain.FinancialConfiguration;
using InventoryApi.Data;
using Microsoft.EntityFrameworkCore;
using SiteCommissionAgreementEntity = InventoryApi.Models.SiteCommissionAgreement;

namespace InventoryApi.Adapters.Persistence;

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

    public async Task<IReadOnlyList<CommissionPayment>> GetPaymentsAsync(
        long? siteId,
        DateTime periodStart,
        DateTime periodEnd,
        CancellationToken cancellationToken) =>
        (await _db.CommissionPayments.AsNoTracking()
            .Where(payment =>
                (!siteId.HasValue || payment.SiteId == siteId.Value) &&
                payment.PeriodStart == periodStart &&
                payment.PeriodEnd == periodEnd)
            .OrderBy(payment => payment.PaymentDate)
            .ThenBy(payment => payment.Id)
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

    public async Task<CommissionPayment> AddPaymentAsync(
        CommissionPayment payment,
        CancellationToken cancellationToken)
    {
        var entity = new InventoryApi.Models.CommissionPayment
        {
            SiteId = payment.SiteId,
            PeriodStart = payment.PeriodStart,
            PeriodEnd = payment.PeriodEnd,
            PaymentDate = payment.PaymentDate,
            Amount = payment.Amount,
            Notes = payment.Notes
        };
        _db.CommissionPayments.Add(entity);
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

    private static CommissionPayment ToDomain(InventoryApi.Models.CommissionPayment entity) =>
        new(
            entity.Id,
            entity.SiteId,
            entity.PeriodStart,
            entity.PeriodEnd,
            entity.PaymentDate,
            entity.Amount,
            entity.Notes,
            entity.CreatedAt,
            entity.UpdatedAt);
}
