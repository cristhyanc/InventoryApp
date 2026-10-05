using Inventory.Application.Reporting.MachineProfitability;
using Inventory.Application.Commissions;
using Inventory.Application.NayaxProcessingFees;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Adapters.Persistence;

/// <summary>
/// Temporary EF Core implementation of <see cref="IMachineProfitabilityReportFactsProvider"/>. It
/// still lives in InventoryApi, not Inventory.Infrastructure: <see cref="AppDbContext"/> and the
/// persistence models it depends on moved there in issue #307, and moving this adapter family after
/// them is Persistence 7/8 and 8/8 of #153. It also composes the Application-owned
/// <see cref="IGetNayaxProcessingFees"/> and <see cref="IGetSiteCommissionReport"/> use cases.
///
/// Its completed-sale query and site-commission resolution are shared with
/// <see cref="EfBookkeepingReportFactsProvider"/> through <see cref="EfReportingSharedQueries"/>.
/// Its per-machine operating-expense breakdown has no equivalent in the already-migrated adapters
/// (bookkeeping only needs the whole-scope total) and stays local to this adapter. Card/cash
/// classification is not EF-translatable, so this adapter materializes only the required sale
/// columns and classifies/groups them in memory, matching the pattern already used by
/// <see cref="EfBookkeepingReportFactsProvider"/>.
/// </summary>
public sealed class EfMachineProfitabilityReportFactsProvider : IMachineProfitabilityReportFactsProvider
{
    private readonly AppDbContext _db;
    private readonly IGetNayaxProcessingFees _nayaxProcessingFees;
    private readonly IGetSiteCommissionReport _siteCommissions;

    public EfMachineProfitabilityReportFactsProvider(
        AppDbContext db, IGetNayaxProcessingFees nayaxProcessingFees, IGetSiteCommissionReport siteCommissions)
    {
        _db = db;
        _nayaxProcessingFees = nayaxProcessingFees;
        _siteCommissions = siteCommissions;
    }

    public async Task<MachineProfitabilityReportFacts> GetFactsAsync(DateTime from, DateTime to, long? machineId, CancellationToken cancellationToken)
    {
        var endExclusive = to.Date.AddDays(1);

        var saleRows = await EfReportingSharedQueries.SalesQuery(_db, from, endExclusive, machineId)
            .Select(x => new { x.MachineID, x.MachineName, x.PaymentMethod, x.SettlementValue, x.CostOfGoodsSold })
            .ToListAsync(cancellationToken);

        var machineGroups = saleRows
            .GroupBy(x => new { x.MachineID, x.MachineName })
            .Select(g => new
            {
                g.Key.MachineID,
                g.Key.MachineName,
                CardSales = g.Where(x => PaymentMethodClassifier.Classify(x.PaymentMethod) == NayaxPaymentType.Card).Sum(x => x.SettlementValue),
                CashSales = g.Where(x => PaymentMethodClassifier.Classify(x.PaymentMethod) == NayaxPaymentType.Cash).Sum(x => x.SettlementValue),
                Sales = g.Sum(x => x.SettlementValue),
                Quantity = g.Count(),
                PartialCost = g.Sum(x => x.CostOfGoodsSold ?? 0m),
                IsCogsComplete = g.All(x => x.CostOfGoodsSold.HasValue),
                UncostedTransactionCount = g.Count(x => !x.CostOfGoodsSold.HasValue),
                UncostedSalesAmount = g.Where(x => !x.CostOfGoodsSold.HasValue).Sum(x => x.SettlementValue),
                Transactions = g.Count()
            })
            .OrderByDescending(x => x.Sales)
            .ToList();

        var commissions = await EfReportingSharedQueries.GetMachineCommissionsAsync(
            _db, _siteCommissions, from, to, endExclusive, machineId, cancellationToken);
        var operatingExpensesByMachine = await OperatingExpensesByMachineAsync(from, endExclusive, cancellationToken);

        var machines = new List<MachineProfitabilityMachineFacts>();
        var missingFeeRateTransactions = 0;
        foreach (var group in machineGroups)
        {
            var fees = await _nayaxProcessingFees.Handle(from, to, group.MachineID, cancellationToken);
            missingFeeRateTransactions += fees.MissingRateTransactionCount;
            var commission = commissions.Machines.GetValueOrDefault(group.MachineID);
            machines.Add(new MachineProfitabilityMachineFacts(
                group.MachineID, group.MachineName ?? $"Machine {group.MachineID}", group.Sales,
                group.CardSales, group.CashSales, group.Quantity, group.PartialCost, group.IsCogsComplete,
                group.UncostedTransactionCount, group.UncostedSalesAmount, group.Transactions,
                commission.Percent, commission.Due, commission.IsComplete,
                operatingExpensesByMachine.GetValueOrDefault(group.MachineID), fees));
        }

        return new MachineProfitabilityReportFacts(machines, missingFeeRateTransactions, commissions.IsComplete, commissions.Warnings);
    }

    private async Task<Dictionary<long, decimal>> OperatingExpensesByMachineAsync(DateTime from, DateTime endExclusive, CancellationToken cancellationToken) =>
        await _db.OperatingExpenses.AsNoTracking()
            .Where(x => x.ExpenseDate >= from && x.ExpenseDate < endExclusive && x.MachineId.HasValue)
            .GroupBy(x => x.MachineId!.Value)
            .Select(g => new { MachineId = g.Key, Total = g.Sum(x => x.TotalAmount) })
            .ToDictionaryAsync(x => x.MachineId, x => x.Total, cancellationToken);
}
