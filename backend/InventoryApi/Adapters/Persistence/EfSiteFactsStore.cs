using Inventory.Application.Sites;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.Sites;
using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Adapters.Persistence;

/// <summary>
/// Temporary EF Core implementation of <see cref="ISiteFactsStore"/>. It still lives in
/// InventoryApi, not Inventory.Infrastructure: <see cref="AppDbContext"/> and the persistence models
/// it depends on moved there in issue #307, and moving this adapter family after them is
/// Persistence 7/8 and 8/8 of #153. It applies the Domain-owned financial rules through its
/// Application port (see <c>docs/architecture.md</c>).
/// </summary>
public sealed class EfSiteFactsStore : ISiteFactsStore
{
    private readonly AppDbContext _db;

    public EfSiteFactsStore(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<SiteProductActivityFact>> GetProductActivityAsync(CancellationToken cancellationToken) =>
        await _db.Products.AsNoTracking()
            .Select(product => new SiteProductActivityFact(product.Id, product.IsActive))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<SiteCompletedSaleFact>> GetRecentCompletedSalesAsync(
        IReadOnlyCollection<long> machineIds, DateTime since, CancellationToken cancellationToken)
    {
        var sales = await _db.NayaxSales.AsNoTracking()
            .Where(sale => machineIds.Contains(sale.MachineID) && sale.MachineAuthorizationTime > since)
            .ToListAsync(cancellationToken);

        return sales
            .Where(sale => NayaxTransactionStatusClassifier.IsCompletedSale(sale.TransactionStatusId))
            .Select(sale => new SiteCompletedSaleFact(sale.MachineID, sale.SettlementValue, sale.MachineAuthorizationTime))
            .ToList();
    }

    public async Task<IReadOnlyDictionary<long, SiteProductCostBasis>> GetProductCostBasisAsync(CancellationToken cancellationToken) =>
        await _db.Products.AsNoTracking()
            .ToDictionaryAsync(
                product => product.Id,
                product => new SiteProductCostBasis(product.Name, product.AverageUnitCost, product.AverageUnitCost > 0m),
                cancellationToken);

    public async Task<SiteCardCommissionResolution> ResolveCardCommissionAsync(
        long siteId, DateTime asOfDate, IReadOnlyCollection<decimal> candidateRetailPrices, CancellationToken cancellationToken)
    {
        var agreementEntities = await _db.SiteCommissionAgreements.AsNoTracking()
            .Where(agreement => agreement.SiteId == siteId)
            .ToListAsync(cancellationToken);
        var agreements = agreementEntities.Select(ToDomain).ToList();

        CommissionAgreement? agreement;
        bool configurationUnavailable;
        try
        {
            agreement = EffectiveFinancialConfiguration.ResolveAgreement(agreements, siteId, asOfDate);
            configurationUnavailable = agreement is null && agreements.Count > 0;
        }
        catch (InvalidOperationException)
        {
            agreement = null;
            configurationUnavailable = true;
        }

        if (configurationUnavailable || agreement is null)
            return new SiteCardCommissionResolution(configurationUnavailable, new Dictionary<decimal, decimal>());

        var resolvedAgreement = agreement;
        return new SiteCardCommissionResolution(
            false,
            candidateRetailPrices.ToDictionary(
                price => price,
                price => SiteCommissionCalculator.CommissionAmount(resolvedAgreement, price, NayaxPaymentType.Card)));
    }

    public async Task<decimal?> ResolveEffectiveFeeExGstAsync(DateTime asOfDate, CancellationToken cancellationToken)
    {
        var rates = await _db.NayaxProcessingFeeRates.AsNoTracking()
            .Where(rate => rate.EffectiveFrom <= asOfDate)
            .OrderBy(rate => rate.EffectiveFrom)
            .Select(rate => new EffectiveNayaxFeeRate(rate.EffectiveFrom, rate.FeeExGst))
            .ToListAsync(cancellationToken);

        return EffectiveFinancialConfiguration.ResolveNayaxFeeRate(rates, asOfDate)?.FeeExGst;
    }

    private static CommissionAgreement ToDomain(Inventory.Infrastructure.Models.SiteCommissionAgreement agreement) =>
        new(
            agreement.Id,
            agreement.SiteId,
            agreement.EffectiveFrom,
            agreement.EffectiveTo,
            agreement.CommissionRate,
            agreement.Frequency,
            agreement.Basis,
            agreement.PaymentDueDaysAfterPeriodEnd,
            agreement.CreatedAt,
            agreement.UpdatedAt);
}
