using Inventory.Application.Reporting.Dashboard;

namespace InventoryApi.Tests.Application.Reporting.Dashboard;

/// <summary>
/// In-memory fake of the dashboard report facts port, so the use case's composition and
/// quality-note assembly can be tested without EF Core, SQLite, or the commission service.
/// </summary>
public sealed class FakeDashboardReportFactsProvider : IDashboardReportFactsProvider
{
    private readonly DashboardReportFacts _facts;

    public FakeDashboardReportFactsProvider(DashboardReportFacts facts) => _facts = facts;

    public (DateTime From, DateTime To, long? MachineId)? LastRequest { get; private set; }

    public Task<DashboardReportFacts> GetFactsAsync(DateTime from, DateTime to, long? machineId, CancellationToken cancellationToken)
    {
        LastRequest = (from, to, machineId);
        return Task.FromResult(_facts);
    }

    public static DashboardReportFacts Complete(int transactionCount = 10, int machineCount = 2, int productCount = 3,
        bool importedContainsRows = true, decimal importedNetSettlement = 74m, bool commissionIsComplete = true) => new(
        TransactionCount: transactionCount,
        MachineCount: machineCount,
        ProductCount: productCount,
        ImportedContainsRows: importedContainsRows,
        ImportedNetSettlement: importedNetSettlement,
        CommissionIsComplete: commissionIsComplete,
        CommissionWarnings: Array.Empty<string>());
}
