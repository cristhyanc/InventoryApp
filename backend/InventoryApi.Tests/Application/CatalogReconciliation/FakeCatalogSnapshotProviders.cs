using Inventory.Application.CatalogReconciliation;
using Inventory.Domain.CatalogReconciliation;

namespace InventoryApi.Tests.Application.CatalogReconciliation;

/// <summary>
/// In-memory fake of the Nayax catalog port, so the reconciliation use-case test exercises
/// orchestration without depending on the real Nayax client.
/// </summary>
public sealed class FakeNayaxCatalogSnapshotProvider : INayaxCatalogSnapshotProvider
{
    public IReadOnlyList<RemoteCatalogEntry> Products { get; set; } = [];
    public IReadOnlyList<RemoteCatalogEntry> Machines { get; set; } = [];

    public Task<IReadOnlyList<RemoteCatalogEntry>> GetProductsAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Products);

    public Task<IReadOnlyList<RemoteCatalogEntry>> GetMachinesAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Machines);
}

/// <summary>In-memory fake of the local catalog history port.</summary>
public sealed class FakeLocalCatalogSnapshotProvider : ILocalCatalogSnapshotProvider
{
    public IReadOnlyList<LocalCatalogEntry> Products { get; set; } = [];
    public IReadOnlyList<LocalCatalogEntry> Machines { get; set; } = [];

    public Task<IReadOnlyList<LocalCatalogEntry>> GetProductsAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Products);

    public Task<IReadOnlyList<LocalCatalogEntry>> GetMachinesAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Machines);
}
