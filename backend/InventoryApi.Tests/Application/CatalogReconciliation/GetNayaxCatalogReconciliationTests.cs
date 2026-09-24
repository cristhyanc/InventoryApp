using Inventory.Application.CatalogReconciliation;
using Inventory.Domain.CatalogReconciliation;
using Xunit;

namespace InventoryApi.Tests.Application.CatalogReconciliation;

public class GetNayaxCatalogReconciliationTests
{
    [Fact]
    public async Task Handle_reconciles_products_and_machines_independently_and_reports_issue_counts()
    {
        var remote = new FakeNayaxCatalogSnapshotProvider
        {
            Products = [new RemoteCatalogEntry(1, "Coke Zero"), new RemoteCatalogEntry(2, "Chips")],
            Machines = [new RemoteCatalogEntry(100, "Machine A")]
        };
        var local = new FakeLocalCatalogSnapshotProvider
        {
            Products = [new LocalCatalogEntry(1, "Coke Zero"), new LocalCatalogEntry(3, "Discontinued Bar")],
            Machines = [new LocalCatalogEntry(100, "Machine A")]
        };
        var handler = new GetNayaxCatalogReconciliation(remote, local);

        var result = await handler.Handle(CancellationToken.None);

        Assert.Equal(3, result.Products.Count);
        Assert.Contains(result.Products, p => p.ExternalId == 1 && p.State == "Present");
        Assert.Contains(result.Products, p => p.ExternalId == 2 && p.State == "Added");
        Assert.Contains(result.Products, p => p.ExternalId == 3 && p.State == "MissingRemotely");
        Assert.Equal(2, result.ProductIssueCount);

        var machine = Assert.Single(result.Machines);
        Assert.Equal(100, machine.ExternalId);
        Assert.Equal("Present", machine.State);
        Assert.Equal(0, result.MachineIssueCount);
    }

    /// <summary>
    /// Regression for the repair of PR #139: a machine renamed in local history whose latest name now
    /// agrees with Nayax is not an issue in the report, and its earlier name reaches the API as
    /// context rather than as a conflict.
    /// </summary>
    [Fact]
    public async Task Handle_reports_a_renamed_machine_that_now_matches_Nayax_as_Present_with_its_earlier_name_as_context()
    {
        var remote = new FakeNayaxCatalogSnapshotProvider
        {
            Machines = [new RemoteCatalogEntry(100, "Machine A At Depot")]
        };
        var local = new FakeLocalCatalogSnapshotProvider
        {
            Machines = [new LocalCatalogEntry(100, "Machine A At Depot", HistoricalNames: ["Machine A"])]
        };
        var handler = new GetNayaxCatalogReconciliation(remote, local);

        var result = await handler.Handle(CancellationToken.None);

        var machine = Assert.Single(result.Machines);
        Assert.Equal("Present", machine.State);
        Assert.Equal("Machine A At Depot", machine.LocalName);
        Assert.Equal(["Machine A"], machine.HistoricalLocalNames);
        Assert.Equal(0, result.MachineIssueCount);
    }

    [Fact]
    public async Task Handle_returns_empty_reports_when_neither_side_has_any_identity()
    {
        var handler = new GetNayaxCatalogReconciliation(new FakeNayaxCatalogSnapshotProvider(), new FakeLocalCatalogSnapshotProvider());

        var result = await handler.Handle(CancellationToken.None);

        Assert.Empty(result.Products);
        Assert.Empty(result.Machines);
    }
}
