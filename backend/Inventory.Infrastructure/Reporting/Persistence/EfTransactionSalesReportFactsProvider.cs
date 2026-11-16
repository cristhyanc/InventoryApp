using System.Runtime.CompilerServices;
using Inventory.Application.Nayax;
using Inventory.Application.Reporting.Transactions;
using Inventory.Application.Time;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.NayaxSqs;
using Inventory.Domain.Reporting.Transactions;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Sites;
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
/// <see cref="EfReportingSharedQueries"/>'s sale helpers because transactions needs every status, not
/// only completed sales; it does reuse <see cref="EfReportingSharedQueries.SalesWindow"/>, so its
/// range resolution is the one every money report shares.
///
/// <para>Since issue #1252 the requested dates are inclusive <em>business</em> dates and the sales
/// are selected between the UTC instants those business days cover. Each streamed row also carries
/// the business date its instant falls on, taken from the one calendar port, so the use case can
/// resolve the sale's fee rate and commission agreement by that date rather than by the UTC date the
/// instant happens to land on.</para>
/// </summary>
public sealed class EfTransactionSalesReportFactsProvider : ITransactionSalesReportFactsProvider
{
    private readonly AppDbContext _db;
    private readonly IBusinessCalendar _businessCalendar;
    private readonly INayaxLynxClient? _nayaxLynxClient;

    public EfTransactionSalesReportFactsProvider(
        AppDbContext db, IBusinessCalendar businessCalendar, INayaxLynxClient? nayaxLynxClient = null)
    {
        _db = db;
        _businessCalendar = businessCalendar;
        _nayaxLynxClient = nayaxLynxClient;
    }

    public async Task<TransactionSalesReportFacts> GetFactsAsync(DateTime from, DateTime to, long? machineId, CancellationToken cancellationToken)
    {
        var window = EfReportingSharedQueries.SalesWindow(_businessCalendar, from, to);
        var startUtc = window.StartUtc;
        var endExclusiveUtc = window.EndExclusiveUtc;

        var salesQuery = _db.NayaxSales.AsNoTracking()
            .Where(x => x.MachineAuthorizationTime >= startUtc && x.MachineAuthorizationTime < endExclusiveUtc &&
                (!machineId.HasValue || x.MachineID == machineId.Value));

        var catalogue = await _db.Products.AsNoTracking()
            .Select(x => new TransactionSalesCatalogueEntry(x.Id, x.Name))
            .ToListAsync(cancellationToken);

        // Every rate that could still be effective on one of the requested business dates. A rate
        // that only takes effect after the last of them cannot apply to any sale in the window.
        var lastBusinessDate = window.LastBusinessDate;
        var feeRateRows = await _db.NayaxProcessingFeeRates.AsNoTracking()
            .Where(x => x.EffectiveFrom <= lastBusinessDate)
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

        // Whether the period holds any stored sale at all, over every status and before the report's
        // own status filter, because that is what separates "no sales data" from "an unexplained
        // zero" (issue #731). It is a separate Any() rather than a count taken while the rows stream
        // past: the state is reported beside the totals, and a state decided from the rows the
        // filter happened to keep would make a payment-type or status filter change it.
        var containsSalesRows = await salesQuery.AnyAsync(cancellationToken);
        // Judged over exactly the instants the sales above were selected between, never the plain
        // calendar range a business day straddles (issue #1250's rule, issue #1252's caller).
        var coverage = await EfReportingSharedQueries.CoverageFactsAsync(
            _db, window, containsSalesRows, containsSettlementRows: false, cancellationToken);

        var rows = StreamRows(salesQuery, machineById, machinesBySite, _businessCalendar, cancellationToken);

        return new TransactionSalesReportFacts(rows, catalogue, feeRates, agreementRows, siteMappingUnavailable)
        {
            Coverage = coverage
        };
    }

    // Consumes the date/machine-filtered EF query as an asynchronous stream instead of completing it
    // with ToListAsync, so the Application use case can product-match and totals-accumulate each raw
    // transaction as it arrives instead of first materialising the complete transaction list.
    private static async IAsyncEnumerable<TransactionSalesReportFactsRow> StreamRows(
        IQueryable<NayaxSales> salesQuery,
        Dictionary<long, NayaxMachine> machineById,
        Dictionary<long, List<NayaxMachine>> machinesBySite,
        IBusinessCalendar businessCalendar,
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
                sale.TransactionID, sale.MachineAuthorizationTime,
                // The business date the sale's instant falls on, from the one calendar port
                // (issue #1252): the date the report's effective-dated fee rate and commission
                // agreement are resolved by, and the date the period that counted it is measured in.
                DateTime.SpecifyKind(businessCalendar.ToBusinessDate(sale.MachineAuthorizationTime), DateTimeKind.Unspecified),
                sale.MachineID, sale.MachineName,
                siteId, siteName, sale.ProductId, sale.NayaxProductId, sale.ProductName,
                PaymentMethodClassifier.Classify(sale.PaymentMethod), sale.PaymentMethod, sale.SettlementValue,
                sale.NayaxProductCostPrice, sale.UnitCostAtSale, sale.CostOfGoodsSold,
                sale.CostingStatus.ToString(), CostSourceLabel(sale), hasPersistedCost,
                // Through the counting policy (issue #1105), so a status-12 sale the business's own
                // lastSales contradicted, or its Owner excluded, is listed as excluded - visible, and
                // never counted as a completed sale in the totals.
                NayaxSaleCountingPolicy.Classify(sale.TransactionStatusId, sale.SqsVerification),
                sale.TransactionStatusId,
                NayaxSaleCountingPolicy.Describe(sale.TransactionStatusId, sale.SqsVerification));
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
