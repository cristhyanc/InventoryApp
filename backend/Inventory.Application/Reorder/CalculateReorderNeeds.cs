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
/// Issue #459 additionally keeps the individual machine selections that read returned
/// (<see cref="ReorderNeedsResult.MachineSelections"/>) and the machines it covered
/// (<see cref="ReorderNeedsResult.MachineIds"/>). Nothing about the reorder aggregation changes: they
/// are the same payload the per-product totals are summed from, retained so the home Dashboard's
/// refill and ordering cards can both be answered from this one fleet read instead of a second
/// <c>GetMachineProductsAsync</c> fan-out.
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

        // The individual selections behind those per-product totals, kept from the same read so a
        // caller that needs the machines' current stock levels - the home Dashboard's refill card
        // (issue #459) - does not fan out across the fleet a second time. A concurrent collection,
        // because the fan-out below writes to it from several machine requests at once.
        var selections = new ConcurrentBag<MachineSelectionStock>();

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
                    // The machine the request was made for, not the payload's own nullable MachineID
                    // field, so a selection is always attributable to a machine.
                    selections.Add(new MachineSelectionStock(
                        machine.MachineID,
                        machineProduct.NayaxProductID,
                        machineProduct.PAR ?? 0,
                        machineProduct.MissingStockByMDB ?? 0,
                        machineProduct.VendOutAlertThreshold ?? 0));

                    if (machineProduct.NayaxProductID is not long productId)
                        continue;

                    var missingStock = machineProduct.MissingStockByMDB ?? 0;
                    machineReplenishmentNeedByProductId.AddOrUpdate(
                        productId, missingStock, (_, existing) => existing + missingStock);
                }
            });

        var onOrderQuantityByProductId = await _outstandingOrders.GetOutstandingQuantitiesByProductAsync(cancellationToken);

        return new ReorderNeedsResult(machineReplenishmentNeedByProductId, onOrderQuantityByProductId)
        {
            MachineIds = machines.Select(machine => machine.MachineID).Distinct().Order().ToList(),
            // Ordered, so the same fleet always produces the same result whatever order the
            // concurrent requests happened to complete in.
            MachineSelections = selections
                .OrderBy(selection => selection.MachineId)
                .ThenBy(selection => selection.ProductId)
                .ToList(),
        };
    }
}
