using Inventory.Application.Reporting.MachineProfitability;
using InventoryApi.Data;
using InventoryApi.Services;
using InventoryApi.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Adapters.Persistence;

/// <summary>
/// Temporary EF Core implementation of <see cref="IMachineProfitabilityReportFactsProvider"/>. It
/// lives in InventoryApi, not Inventory.Infrastructure, because it depends on
/// <see cref="AppDbContext"/> and persistence models that still live in InventoryApi, and because
/// it also composes the existing <see cref="INayaxProcessingFeeService"/> and
/// <see cref="ISiteCommissionService"/> business services, which are not part of this migration.
/// Move it into Inventory.Infrastructure once the shared AppDbContext and persistence models
/// relocate there.
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
    private readonly INayaxProcessingFeeService _nayaxProcessingFees;
    private readonly ISiteCommissionService _siteCommissions;

    public EfMachineProfitabilityReportFactsProvider(
        AppDbContext db, INayaxProcessingFeeService nayaxProcessingFees, ISiteCommissionService siteCommissions)
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
            var fees = await _nayaxProcessingFees.GetProcessingFeesAsync(from, to, group.MachineID, cancellationToken);
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
