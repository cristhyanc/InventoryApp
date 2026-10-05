using System.Runtime.CompilerServices;
using Inventory.Application.Nayax;
using Inventory.Application.Reporting.Transactions;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.Reporting.Transactions;
using Inventory.Infrastructure.Sites;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Reporting.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="ITransactionSalesReportFactsProvider"/>. Everything it
/// depends on is owned by this project - <see cref="INayaxLynxClient"/>'s adapter by issue #306,
/// <see cref="AppDbContext"/> and the persistence models by issue #307 - and issue #308
/// (Persistence 7/8 of #153) moved this adapter family after them.
///
/// This adapter returns only raw per-transaction facts, the raw catalogue, and raw effective-dated
/// fee/commission facts (query and external-integration mechanics); it deliberately does not
/// compute product matches, fee/commission/profit derivation, filtering, sorting, or totals, since
/// those are Domain/Application concerns applied by <see cref="GetTransactionSalesReport"/>. Site
/// name resolution from the live Nayax machine directory runs the one authoritative rule, which
/// issue #306 moved to <see cref="SiteNameResolver"/>: this adapter calls its static entry point
/// rather than the injected <c>ISiteNameResolver</c> port because it resolves a name per streamed
/// row inside a static iterator. The completed/all-status sale query intentionally does not reuse
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

        var salesQuery = _db.NayaxSales.AsNoTracking()
            .Where(x => x.MachineAuthorizationTime >= from && x.MachineAuthorizationTime < endExclusive &&
                (!machineId.HasValue || x.MachineID == machineId.Value));

        var catalogue = await _db.Products.AsNoTracking()
            .Select(x => new TransactionSalesCatalogueEntry(x.Id, x.Name))
            .ToListAsync(cancellationToken);

        var feeRateRows = await _db.NayaxProcessingFeeRates.AsNoTracking()
            .Where(x => x.EffectiveFrom <= to)
            .Select(x => new { x.EffectiveFrom, x.FeeExGst })
            .ToListAsync(cancellationToken);
        var feeRates = feeRateRows.Select(x => new EffectiveNayaxFeeRate(x.EffectiveFrom, x.FeeExGst)).ToList();

        var agreementRows = await _db.SiteCommissionAgreements.AsNoTracking()
            .Select(x => new EffectiveCommissionAgreement(
                x.SiteId, x.EffectiveFrom, x.EffectiveTo, x.Basis, x.CommissionRate))
            .ToListAsync(cancellationToken);

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

        var rows = StreamRows(salesQuery, machineById, machinesBySite, cancellationToken);

        return new TransactionSalesReportFacts(rows, catalogue, feeRates, agreementRows, siteMappingUnavailable);
    }

    // Consumes the date/machine-filtered EF query as an asynchronous stream instead of completing it
    // with ToListAsync, so the Application use case can product-match and totals-accumulate each raw
    // transaction as it arrives instead of first materialising the complete transaction list.
    private static async IAsyncEnumerable<TransactionSalesReportFactsRow> StreamRows(
        IQueryable<NayaxSales> salesQuery,
        Dictionary<long, NayaxMachine> machineById,
        Dictionary<long, List<NayaxMachine>> machinesBySite,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var sale in salesQuery.AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            machineById.TryGetValue(sale.MachineID, out var machine);
            var siteId = machine?.CustomerID;
            var siteName = siteId.HasValue && machinesBySite.TryGetValue(siteId.Value, out var siteMachines)
                ? SiteNameResolver.FromMachines(siteMachines, siteId.Value)
                : null;
            var hasPersistedCost = sale.CostingStatus == SaleCostingStatus.Costed && sale.CostOfGoodsSold.HasValue;

            yield return new TransactionSalesReportFactsRow(
                sale.TransactionID, sale.MachineAuthorizationTime, sale.MachineID, sale.MachineName,
                siteId, siteName, sale.NayaxProductId, sale.ProductName,
                PaymentMethodClassifier.Classify(sale.PaymentMethod), sale.PaymentMethod, sale.SettlementValue,
                sale.NayaxProductCostPrice, sale.UnitCostAtSale, sale.CostOfGoodsSold,
                sale.CostingStatus.ToString(), CostSourceLabel(sale), hasPersistedCost,
                NayaxTransactionStatusClassifier.Classify(sale.TransactionStatusId),
                sale.TransactionStatusId, NayaxTransactionStatusClassifier.Describe(sale.TransactionStatusId));
        }
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

}
