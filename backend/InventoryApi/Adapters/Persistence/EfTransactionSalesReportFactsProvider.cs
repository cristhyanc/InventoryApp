using Inventory.Application.Reporting.Transactions;
using Inventory.Domain.Reporting.Transactions;
using InventoryApi.Data;
using InventoryApi.Integrations.Nayax;
using InventoryApi.Models;
using InventoryApi.Services;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Adapters.Persistence;

/// <summary>
/// Temporary EF Core implementation of <see cref="ITransactionSalesReportFactsProvider"/>. It lives
/// in InventoryApi, not Inventory.Infrastructure, because it depends on <see cref="AppDbContext"/>,
/// persistence models, and <see cref="INayaxLynxClient"/>, which still live in InventoryApi. Move it
/// into Inventory.Infrastructure once the shared AppDbContext and persistence models relocate there.
///
/// This adapter returns only raw per-transaction facts, the raw catalogue, and raw effective-dated
/// fee/commission facts (query and external-integration mechanics); it deliberately does not
/// compute product matches, fee/commission/profit derivation, filtering, sorting, or totals, since
/// those are Domain/Application concerns applied by <see cref="GetTransactionSalesReport"/>. Site
/// name resolution from the live Nayax machine directory has no equivalent already-migrated adapter
/// to share it with; the completed/all-status sale query intentionally does not reuse
/// <see cref="EfReportingSharedQueries"/> because transactions needs every status, not only
/// completed sales.
/// </summary>
public sealed class EfTransactionSalesReportFactsProvider : ITransactionSalesReportFactsProvider
{
    private readonly AppDbContext _db;
    private readonly INayaxLynxClient? _nayaxLynxClient;

    public EfTransactionSalesReportFactsProvider(AppDbContext db, INayaxLynxClient? nayaxLynxClient = null)
    {
        _db = db;
        _nayaxLynxClient = nayaxLynxClient;
    }

    public async Task<TransactionSalesReportFacts> GetFactsAsync(DateTime from, DateTime to, long? machineId, CancellationToken cancellationToken)
    {
        var endExclusive = to.Date.AddDays(1);

        var sales = await _db.NayaxSales.AsNoTracking()
            .Where(x => x.MachineAuthorizationTime >= from && x.MachineAuthorizationTime < endExclusive &&
                (!machineId.HasValue || x.MachineID == machineId.Value))
            .ToListAsync(cancellationToken);

        var catalogue = await _db.Products.AsNoTracking()
            .Select(x => new TransactionSalesCatalogueEntry(x.Id, x.Name))
            .ToListAsync(cancellationToken);

        var feeRateRows = await _db.NayaxProcessingFeeRates.AsNoTracking()
            .Where(x => x.EffectiveFrom <= to)
            .Select(x => new { x.EffectiveFrom, x.FeeExGst })
            .ToListAsync(cancellationToken);
        var feeRates = feeRateRows.Select(x => new EffectiveFeeRate(x.EffectiveFrom, x.FeeExGst)).ToList();

        var agreementRows = await _db.SiteCommissionAgreements.AsNoTracking()
            .Select(x => new { x.SiteId, x.EffectiveFrom, x.EffectiveTo, x.Basis, x.CommissionRate })
            .ToListAsync(cancellationToken);
        var agreements = agreementRows
            .Select(x => new EffectiveCommissionAgreement(x.SiteId, x.EffectiveFrom, x.EffectiveTo, ToDomain(x.Basis), x.CommissionRate))
            .ToList();

        var siteMappingUnavailable = _nayaxLynxClient is null;
        var liveMachines = new List<NayaxMachine>();
        if (_nayaxLynxClient is not null)
        {
            try
            {
                liveMachines = await _nayaxLynxClient.GetMachinesAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                siteMappingUnavailable = true;
            }
        }

        var machineById = liveMachines.GroupBy(x => x.MachineID).ToDictionary(x => x.Key, x => x.First());
        var machinesBySite = liveMachines.Where(x => x.CustomerID.HasValue)
            .GroupBy(x => x.CustomerID!.Value)
            .ToDictionary(x => x.Key, x => x.ToList());

        var rows = sales.Select(sale =>
        {
            machineById.TryGetValue(sale.MachineID, out var machine);
            var siteId = machine?.CustomerID;
            var siteName = siteId.HasValue && machinesBySite.TryGetValue(siteId.Value, out var siteMachines)
                ? SiteNameResolver.FromMachines(siteMachines, siteId.Value)
                : null;
            var hasPersistedCost = sale.CostingStatus == SaleCostingStatus.Costed && sale.CostOfGoodsSold.HasValue;

            return new TransactionSalesReportFactsRow(
                sale.TransactionID, sale.MachineAuthorizationTime, sale.MachineID, sale.MachineName,
                siteId, siteName, sale.NayaxProductId, sale.ProductName,
                ToDomain(PaymentMethodClassifier.Classify(sale.PaymentMethod)), sale.PaymentMethod, sale.SettlementValue,
                sale.NayaxProductCostPrice, sale.UnitCostAtSale, sale.CostOfGoodsSold,
                sale.CostingStatus.ToString(), CostSourceLabel(sale), hasPersistedCost,
                ToDomain(NayaxTransactionStatusClassifier.Classify(sale.TransactionStatusId)),
                sale.TransactionStatusId, NayaxTransactionStatusClassifier.Describe(sale.TransactionStatusId));
        }).ToList();

        return new TransactionSalesReportFacts(rows, catalogue, feeRates, agreements, siteMappingUnavailable);
    }

    private static string CostSourceLabel(NayaxSales sale)
    {
        if (sale.CostingStatus == SaleCostingStatus.Pending)
            return "Pending";
        if (sale.CostingStatus == SaleCostingStatus.LegacyEstimated)
            return "Estimated";

        return sale.CostSource switch
        {
            SaleCostSource.InventoryLedger => "Inventory Ledger",
            SaleCostSource.NayaxTransactionExport => "Nayax Historical Export",
            SaleCostSource.Estimated => "Estimated",
            _ => "Unknown"
        };
    }

    private static TransactionPaymentType ToDomain(NayaxPaymentType type) => type switch
    {
        NayaxPaymentType.Card => TransactionPaymentType.Card,
        NayaxPaymentType.Cash => TransactionPaymentType.Cash,
        _ => TransactionPaymentType.Unknown
    };

    private static TransactionSaleStatus ToDomain(NayaxTransactionStatus status) => status switch
    {
        NayaxTransactionStatus.Completed => TransactionSaleStatus.Completed,
        NayaxTransactionStatus.Pending => TransactionSaleStatus.Pending,
        NayaxTransactionStatus.Refunded => TransactionSaleStatus.Refunded,
        NayaxTransactionStatus.CancelledOrDeclined => TransactionSaleStatus.CancelledOrDeclined,
        _ => TransactionSaleStatus.Unknown
    };

    private static TransactionCommissionBasis ToDomain(CommissionBasis basis) => basis switch
    {
        CommissionBasis.CardSales => TransactionCommissionBasis.CardSales,
        CommissionBasis.SalesExGst => TransactionCommissionBasis.SalesExGst,
        _ => TransactionCommissionBasis.GrossSales
    };
}
