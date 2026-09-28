using System.Collections.Concurrent;
using Inventory.Application.Nayax;

namespace Inventory.Application.Reorder;

/// <summary>
/// The reorder-alert aggregation use case (issue #47): fetches the live machine fleet and, for each
/// machine, its current Nayax product mappings through the Application-owned <see cref="INayaxLynxClient"/>
/// port, summing <c>MissingStockByMDB</c> per catalogue product ID exactly as the legacy
/// <c>InventoryApi.Services.ProductService.LowStock</c> loop did, and combines it with outstanding
/// supplier-order quantity from <see cref="IOutstandingSupplierOrderQuantityStore"/>. It has no
/// knowledge of which products a caller's search/category/supplier filter selected: both dictionaries
/// are keyed by product ID over the whole catalogue/machine fleet, so a caller looks up only the
/// products it cares about.
///
/// The per-machine <c>GetMachineProductsAsync</c> calls run with bounded parallelism
/// (<see cref="MaxConcurrentMachineRequests"/>) rather than one request per machine in sequence or an
/// unbounded fan-out, chosen over a cache/snapshot because the current machine fleet is small and a
/// snapshot would add freshness/invalidation semantics the issue asks to avoid absent clear evidence
/// bounded parallelism is unsuitable. A failing machine-product request (typed as
/// <c>Inventory.Infrastructure.Nayax.NayaxUpstreamException</c> at the HTTP adapter) or caller
/// cancellation propagates out of <see cref="Handle"/> unchanged rather than being absorbed into a
/// partial result, so a partial aggregate is never presented as a complete one.
/// </summary>
public sealed class CalculateReorderNeeds
{
    /// <summary>
    /// The upper bound on simultaneous live <c>GetMachineProductsAsync</c> requests. A fixed
    /// engineering constant, not environment configuration: the current machine fleet is small enough
    /// that this is chosen for safety headroom against Nayax rate limits, not for throughput tuning.
    /// </summary>
    public const int MaxConcurrentMachineRequests = 4;

    private readonly INayaxLynxClient _nayax;
    private readonly IOutstandingSupplierOrderQuantityStore _outstandingOrders;
    private readonly int _maxConcurrentMachineRequests;

    public CalculateReorderNeeds(INayaxLynxClient nayax, IOutstandingSupplierOrderQuantityStore outstandingOrders)
        : this(nayax, outstandingOrders, MaxConcurrentMachineRequests)
    {
    }

    /// <summary>
    /// Overload for a caller that needs a specific concurrency bound (tests proving the limit is
    /// respected). DI resolves the two-argument constructor above, since this one's
    /// <paramref name="maxConcurrentMachineRequests"/> has no registered service to satisfy it.
    /// </summary>
    public CalculateReorderNeeds(
        INayaxLynxClient nayax, IOutstandingSupplierOrderQuantityStore outstandingOrders, int maxConcurrentMachineRequests)
    {
        if (maxConcurrentMachineRequests < 1)
            throw new ArgumentOutOfRangeException(nameof(maxConcurrentMachineRequests), "Must be at least 1.");

        _nayax = nayax;
        _outstandingOrders = outstandingOrders;
        _maxConcurrentMachineRequests = maxConcurrentMachineRequests;
    }

    public async Task<ReorderNeedsResult> Handle(CancellationToken cancellationToken)
    {
        var machines = await _nayax.GetMachinesAsync(cancellationToken);

        var machineReplenishmentNeedByProductId = new ConcurrentDictionary<long, int>();

        await Parallel.ForEachAsync(
            machines,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = _maxConcurrentMachineRequests,
                CancellationToken = cancellationToken,
            },
            async (machine, machineCancellationToken) =>
            {
                var machineProducts = await _nayax.GetMachineProductsAsync(machine.MachineID, machineCancellationToken);
                foreach (var machineProduct in machineProducts)
                {
                    if (machineProduct.NayaxProductID is not long productId)
                        continue;

                    var missingStock = machineProduct.MissingStockByMDB ?? 0;
                    machineReplenishmentNeedByProductId.AddOrUpdate(
                        productId, missingStock, (_, existing) => existing + missingStock);
                }
            });

        var onOrderQuantityByProductId = await _outstandingOrders.GetOutstandingQuantitiesByProductAsync(cancellationToken);

        return new ReorderNeedsResult(machineReplenishmentNeedByProductId, onOrderQuantityByProductId);
    }
}
