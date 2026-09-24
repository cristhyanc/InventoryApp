using Inventory.Domain.CatalogReconciliation;

namespace Inventory.Application.CatalogReconciliation;

/// <summary>
/// The Nayax catalog reconciliation use case (issue #55): compares current Nayax products/machines
/// against local history through the narrow <see cref="INayaxCatalogSnapshotProvider"/>/
/// <see cref="ILocalCatalogSnapshotProvider"/> ports, applies the deterministic Domain
/// <see cref="CatalogReconciliationPolicy"/>, and returns the result for an admin/data-quality view.
/// Read-only: it never writes to local storage and never calls Nayax to change anything.
/// </summary>
public sealed class GetNayaxCatalogReconciliation
{
    private readonly INayaxCatalogSnapshotProvider _remote;
    private readonly ILocalCatalogSnapshotProvider _local;

    public GetNayaxCatalogReconciliation(INayaxCatalogSnapshotProvider remote, ILocalCatalogSnapshotProvider local)
    {
        _remote = remote;
        _local = local;
    }

    public async Task<CatalogReconciliationReportDto> Handle(CancellationToken cancellationToken)
    {
        var remoteProductsTask = _remote.GetProductsAsync(cancellationToken);
        var remoteMachinesTask = _remote.GetMachinesAsync(cancellationToken);
        var localProductsTask = _local.GetProductsAsync(cancellationToken);
        var localMachinesTask = _local.GetMachinesAsync(cancellationToken);
        await Task.WhenAll(remoteProductsTask, remoteMachinesTask, localProductsTask, localMachinesTask);

        var products = CatalogReconciliationPolicy.Reconcile(await localProductsTask, await remoteProductsTask);
        var machines = CatalogReconciliationPolicy.Reconcile(await localMachinesTask, await remoteMachinesTask);

        return new CatalogReconciliationReportDto(ToDtos(products), ToDtos(machines));
    }

    private static IReadOnlyList<CatalogReconciliationEntryDto> ToDtos(IReadOnlyList<ReconciliationEntry> entries) =>
        entries
            .Select(entry => new CatalogReconciliationEntryDto(
                entry.ExternalId,
                entry.State.ToString(),
                entry.LocalName,
                entry.RemoteName,
                entry.HistoricalLocalNames,
                entry.Note))
            .ToList();
}
