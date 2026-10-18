using Inventory.Application.Reporting.MachineProfitability;
using Inventory.Application.Reporting.Shared;
using InventoryApi.Tests.Application.Reporting.Shared;

namespace InventoryApi.Tests.Application.Reporting.MachineProfitability;

/// <summary>
/// In-memory fake of the report facts port, so the machine profitability use case's orchestration
/// and quality-note assembly can be tested without EF Core, SQLite, or the Nayax/commission services.
/// </summary>
public sealed class FakeMachineProfitabilityReportFactsProvider : IMachineProfitabilityReportFactsProvider
{
    private readonly MachineProfitabilityReportFacts _facts;

    public FakeMachineProfitabilityReportFactsProvider(MachineProfitabilityReportFacts facts) => _facts = facts;

    public (DateTime From, DateTime To, long? MachineId)? LastRequest { get; private set; }

    public Task<MachineProfitabilityReportFacts> GetFactsAsync(DateTime from, DateTime to, long? machineId, CancellationToken cancellationToken)
    {
        LastRequest = (from, to, machineId);
        return Task.FromResult(_facts);
    }

    public static MachineProfitabilityReportFacts Empty(ReportCoverageFacts? coverage = null) =>
        new(Array.Empty<MachineProfitabilityMachineFacts>(), 0, true, Array.Empty<string>())
        {
            Coverage = coverage ?? TestReportCoverage.Complete()
        };

    public static MachineProfitabilityReportFacts SingleMachine(
        long machineId = 10, string machineName = "Machine A", decimal sales = 100m, decimal cost = 40m,
        decimal cardSales = 80m, decimal cashSales = 20m, bool isCogsComplete = true,
        decimal commissionDue = 5m, bool commissionComplete = true, decimal operatingExpenses = 2m,
        bool hasMissingFeeRates = false, ReportCoverageFacts? coverage = null) => new(
        [
            new MachineProfitabilityMachineFacts(machineId, machineName, sales, cardSales, cashSales, 10m,
                cost, isCogsComplete, isCogsComplete ? 0 : 1, isCogsComplete ? 0m : sales, 10,
                commissionDue / (sales == 0m ? 1m : sales), commissionDue, commissionComplete, operatingExpenses,
                new NayaxProcessingFeeResult(4m, 0.4m, 4.4m, 0m, 0m, 0m, 0, DateTime.UtcNow, null,
                    hasMissingFeeRates ? 1 : 0))
        ],
        hasMissingFeeRates ? 1 : 0, commissionComplete, Array.Empty<string>())
        {
            // Fully accounted for unless a test replaces it, so a test about something else is
            // never incidentally about provider coverage (issue #731).
            Coverage = coverage ?? TestReportCoverage.Complete()
        };
}
