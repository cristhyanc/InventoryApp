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
        // The two remote reads stay a concurrent fan-out, and are awaited before the local reads
        // start (issue #667). Since issue #520 a Nayax call resolves the business's credentials
        // through the request's own scoped AppDbContext, so a remote read in flight beside a local
        // one is two operations on one context, which it supports no more than two local reads
        // would (docs/architecture.md § Concurrency inside one request). The local reads are then
        // awaited one at a time, as that rule has always required.
        var remoteProductsTask = _remote.GetProductsAsync(cancellationToken);
        var remoteMachinesTask = _remote.GetMachinesAsync(cancellationToken);
        await Task.WhenAll(remoteProductsTask, remoteMachinesTask);

        var localProducts = await _local.GetProductsAsync(cancellationToken);
        var localMachines = await _local.GetMachinesAsync(cancellationToken);

        var products = CatalogReconciliationPolicy.Reconcile(localProducts, await remoteProductsTask);
        var machines = CatalogReconciliationPolicy.Reconcile(localMachines, await remoteMachinesTask);

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
