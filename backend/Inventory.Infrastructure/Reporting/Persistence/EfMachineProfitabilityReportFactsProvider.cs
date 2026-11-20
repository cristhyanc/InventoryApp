using Inventory.Application.Reporting.MachineProfitability;
using Inventory.Application.Commissions;
using Inventory.Application.NayaxProcessingFees;
using Inventory.Application.Time;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Reporting.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IMachineProfitabilityReportFactsProvider"/>. It lives in
/// Inventory.Infrastructure with the <see cref="AppDbContext"/> and persistence models it depends on
/// (issue #307 moved those, issue #308 this adapter family). It also composes the Application-owned
/// <see cref="IGetNayaxProcessingFees"/> and <see cref="IGetSiteCommissionReport"/> use cases.
///
/// Its completed-sale query and site-commission resolution are shared with
/// <see cref="EfBookkeepingReportFactsProvider"/> through <see cref="EfReportingSharedQueries"/>.
/// Its per-machine operating-expense breakdown has no equivalent in the already-migrated adapters
/// (bookkeeping only needs the whole-scope total) and stays local to this adapter. Card/cash
/// classification is not EF-translatable, so this adapter materializes only the required sale
/// columns and classifies/groups them in memory, matching the pattern already used by
/// <see cref="EfBookkeepingReportFactsProvider"/>.
///
/// Since issue #1263 it produces exactly one set of facts per machine: the sales are grouped by the
/// provider's machine number alone, and the row's display name is resolved separately from this
/// business's own machine record (see <see cref="DisplayName"/>). Every per-machine charge - the
/// processing fees, the site commission and the operating expenses - is therefore looked up once per
/// machine, as the machine incurred it once.
///
/// Since issue #1252 it selects its sales by business-date instants, through the same
/// <see cref="EfReportingSharedQueries.SalesWindow"/> bookkeeping uses, and asks the fee use case for
/// that window's own business-day period so each sale is estimated at the rate effective on the
/// business date that counted it. Operating-expense dates are date-only values and keep their plain
/// calendar-date bounds.
/// </summary>
public sealed class EfMachineProfitabilityReportFactsProvider : IMachineProfitabilityReportFactsProvider
{
    private readonly AppDbContext _db;
    private readonly IGetNayaxProcessingFees _nayaxProcessingFees;
    private readonly IGetSiteCommissionReport _siteCommissions;
    private readonly IBusinessCalendar _businessCalendar;

    public EfMachineProfitabilityReportFactsProvider(
        AppDbContext db, IGetNayaxProcessingFees nayaxProcessingFees, IGetSiteCommissionReport siteCommissions,
        IBusinessCalendar businessCalendar)
    {
        _db = db;
        _nayaxProcessingFees = nayaxProcessingFees;
        _siteCommissions = siteCommissions;
        _businessCalendar = businessCalendar;
    }

    public async Task<MachineProfitabilityReportFacts> GetFactsAsync(DateTime from, DateTime to, long? machineId, CancellationToken cancellationToken)
    {
        // Two different kinds of boundary (issue #1250's rule, applied to this report by issue
        // #1252). Sales are selected by instant, over the business days the requested dates name;
        // operating-expense dates are date-only values and keep plain calendar-date bounds.
        var window = EfReportingSharedQueries.SalesWindow(_businessCalendar, from, to);
        var endExclusive = to.Date.AddDays(1);

        var saleRows = await EfReportingSharedQueries.SalesQuery(_db, window, machineId)
            .Select(x => new
            {
                x.MachineID,
                x.MachineName,
                x.TransactionID,
                x.MachineAuthorizationTime,
                x.PaymentMethod,
                x.SettlementValue,
                x.CostOfGoodsSold
            })
            .ToListAsync(cancellationToken);

        // One row per machine, keyed by the provider's machine number alone (issue #1263). The
        // stored sale name is not part of the key: a machine renamed between two sales - "Lobby"
        // and "Lobby (Level 1)" - is one machine, and grouping by the name as well made it two
        // rows that were each charged the machine's whole fees, commission and operating expenses.
        var machineGroups = saleRows
            .GroupBy(x => x.MachineID)
            .Select(g => new
            {
                MachineID = g.Key,
                // The name the latest sale carried, as the fallback for a machine this business has
                // no record of: latest by sale instant, and between two equally recent names the one
                // on the higher transaction id, so the row's name never depends on the order the
                // rows came back in. A sale that carries no name names nothing.
                LatestSaleName = g
                    .Where(x => !string.IsNullOrWhiteSpace(x.MachineName))
                    .OrderByDescending(x => x.MachineAuthorizationTime)
                    .ThenByDescending(x => x.TransactionID)
                    .Select(x => x.MachineName)
                    .FirstOrDefault(),
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
            _db, _siteCommissions, window, machineId, cancellationToken);
        // This business's own machine for each number that sold, resolved once: it names the row and
        // it is what an operating expense is recorded against (issue #780).
        var localMachines = await EfMachineLinkQueries.LocalMachinesByNayaxIdAsync(
            _db, machineGroups.ConvertAll(group => group.MachineID), cancellationToken);
        var operatingExpensesByMachine = await OperatingExpensesByMachineAsync(
            from, endExclusive, localMachines, cancellationToken);

        var machines = new List<MachineProfitabilityMachineFacts>();
        var missingFeeRateTransactions = 0;
        foreach (var group in machineGroups)
        {
            // The fees are charged to exactly the sales this machine's revenue counts, and each one
            // is estimated at the rate effective on the business date that counted it (issue #1252,
            // on issue #310's contract and issue #1250's window). One call per machine, because
            // there is one row per machine.
            var fees = await _nayaxProcessingFees.HandleBusinessPeriod(
                window.FeePeriod, group.MachineID, cancellationToken);
            missingFeeRateTransactions += fees.MissingRateTransactionCount;
            var commission = commissions.Machines.GetValueOrDefault(group.MachineID);
            machines.Add(new MachineProfitabilityMachineFacts(
                group.MachineID, DisplayName(group.MachineID, group.LatestSaleName, localMachines), group.Sales,
                group.CardSales, group.CashSales, group.Quantity, group.PartialCost, group.IsCogsComplete,
                group.UncostedTransactionCount, group.UncostedSalesAmount, group.Transactions,
                commission.Percent, commission.Due, commission.IsComplete,
                operatingExpensesByMachine.GetValueOrDefault(group.MachineID), fees));
        }

        // Every stored sale in scope, over every status and before the completed-sale filter, and
        // any imported settlement row overlapping the period (issue #731). A machine with only
        // cancelled rows produces no row at all, which is unverified rather than a measured zero.
        var containsSalesRows = await EfReportingSharedQueries
            .AllSalesQuery(_db, window, machineId).AnyAsync(cancellationToken);
        // Reimbursement coverage dates are date-only values, so the imported summary keeps the
        // plain calendar range; coverage is judged over exactly the instants the sales above were
        // selected between (issue #1250's rule).
        var imported = await EfReportingSharedQueries.ImportedSummaryAsync(
            _db, from, endExclusive, machineId, cancellationToken);
        var coverage = await EfReportingSharedQueries.CoverageFactsAsync(
            _db, window, containsSalesRows, imported.ContainsRows, cancellationToken);

        return new MachineProfitabilityReportFacts(machines, missingFeeRateTransactions, commissions.IsComplete, commissions.Warnings)
        {
            Coverage = coverage
        };
    }

    /// <summary>
    /// What one machine's row is called (issue #1263): the name this business gave the machine in
    /// its own machine record, and otherwise the name its latest sale carried, and otherwise the
    /// provider's machine number.
    ///
    /// <para>The local record is preferred because it is the one name the business chose, while a
    /// stored sale carries whatever the provider called the machine at that moment - which is
    /// exactly what used to split one machine into several rows. The sale-name fallback keeps a name
    /// on the row of a machine this business has no record of (a number the provider reports that no
    /// local machine carries), which is the only case the old name could come from.</para>
    /// </summary>
    private static string DisplayName(
        long providerMachineId, string? latestSaleName, IReadOnlyDictionary<long, LocalMachineLink> localMachines) =>
        localMachines.TryGetValue(providerMachineId, out var local) && !string.IsNullOrWhiteSpace(local.Name)
            ? local.Name
            : latestSaleName ?? $"Machine {providerMachineId}";

    /// <summary>
    /// The operating expenses of each machine in <paramref name="localMachines"/> in the period,
    /// keyed by the <em>provider's</em> machine number - which is what this report's rows are keyed
    /// by and what its filter speaks, and which issue #780 deliberately left unchanged.
    ///
    /// <para>Since issue #780 an expense records the local machine (<c>Machines.Id</c>), not the
    /// provider's number, so the expenses are grouped by the local machine ids the one lookup
    /// (<see cref="EfMachineLinkQueries"/>) resolved for the caller and mapped back onto the
    /// numbers. Every total is the same as before: the <c>AddMachines</c> backfill gave each
    /// referenced number exactly one machine per business, and the repoint migration refused to run
    /// unless every attributed expense matched one. A machine this business has recorded none for
    /// contributes nothing, which is the same answer the unknown-machine case always gave - and
    /// since issue #1263 each machine is charged its expenses once, because it has one row.</para>
    /// </summary>
    private async Task<Dictionary<long, decimal>> OperatingExpensesByMachineAsync(
        DateTime from, DateTime endExclusive, IReadOnlyDictionary<long, LocalMachineLink> localMachines,
        CancellationToken cancellationToken)
    {
        if (localMachines.Count == 0) return [];

        var machineIds = localMachines.Values.Select(link => link.Id).ToList();
        var totalsByLocalMachine = await _db.OperatingExpenses.AsNoTracking()
            .Where(x => x.ExpenseDate >= from && x.ExpenseDate < endExclusive
                && x.MachineId.HasValue && machineIds.Contains(x.MachineId.Value))
            .GroupBy(x => x.MachineId!.Value)
            .Select(g => new { MachineId = g.Key, Total = g.Sum(x => x.TotalAmount) })
            .ToDictionaryAsync(x => x.MachineId, x => x.Total, cancellationToken);

        return localMachines.ToDictionary(
            link => link.Key, link => totalsByLocalMachine.GetValueOrDefault(link.Value.Id));
    }
}
