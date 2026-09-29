using System.Collections.Concurrent;
using Inventory.Application.Nayax;
using Inventory.Application.Reorder;

namespace Inventory.Application.PickList;

/// <summary>
/// The read-only Pick List projection use case (issue #221). For the selected machines it fetches
/// each machine's current live Nayax product mappings through the same
/// <see cref="INayaxLynxClient.GetMachineProductsAsync"/> port and PAR/<c>MissingStockByMDB</c>
/// arithmetic <c>InventoryApi.Services.MachineService.GetMachineProducts</c> already uses for a single
/// machine's product list, aggregates them into a per-product/machine pick matrix, and compares the
/// combined pick quantity per product against its physical storage quantity from
/// <see cref="IPickListStorageStockStore"/>. It creates no inventory movement, refill, or persisted
/// state: it only reads.
///
/// Per-machine calls run with the same bounded parallelism
/// <see cref="CalculateReorderNeeds.MaxConcurrentMachineRequests"/> already established for fanning
/// out <c>GetMachineProductsAsync</c> across the machine fleet (issue #47), reused unchanged here
/// rather than re-deriving a concurrency bound for a second Nayax fan-out.
///
/// A product with no matching local catalogue row (for example, one outside the caller's business)
/// is silently excluded from the result rather than appearing with a fabricated zero storage
/// quantity: <see cref="IPickListStorageStockStore"/> is scoped by the same tenant ownership every
/// other <c>AppDbContext</c> read is, so "not found" there is the tenant boundary, not a data gap to
/// paper over.
/// </summary>
public sealed class GetPickList
{
    private readonly INayaxLynxClient _nayax;
    private readonly IPickListStorageStockStore _storage;
    private readonly int _maxConcurrentMachineRequests;

    public GetPickList(INayaxLynxClient nayax, IPickListStorageStockStore storage)
        : this(nayax, storage, CalculateReorderNeeds.MaxConcurrentMachineRequests)
    {
    }

    /// <summary>
    /// Overload for a caller that needs a specific concurrency bound (tests proving the limit is
    /// respected). DI resolves the two-argument constructor above, since this one's
    /// <paramref name="maxConcurrentMachineRequests"/> has no registered service to satisfy it.
    /// </summary>
    public GetPickList(INayaxLynxClient nayax, IPickListStorageStockStore storage, int maxConcurrentMachineRequests)
    {
        if (maxConcurrentMachineRequests < 1)
            throw new ArgumentOutOfRangeException(nameof(maxConcurrentMachineRequests), "Must be at least 1.");

        _nayax = nayax;
        _storage = storage;
        _maxConcurrentMachineRequests = maxConcurrentMachineRequests;
    }

    public async Task<PickListResult> Handle(IReadOnlyCollection<long> machineIds, CancellationToken cancellationToken)
    {
        var distinctMachineIds = machineIds.Distinct().ToList();
        if (distinctMachineIds.Count == 0)
            return new PickListResult(Array.Empty<PickListProduct>());

        var cellsByProductAndMachine = new ConcurrentDictionary<(long ProductId, long MachineId), PickListCell>();

        await Parallel.ForEachAsync(
            distinctMachineIds,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = _maxConcurrentMachineRequests,
                CancellationToken = cancellationToken,
            },
            async (machineId, machineCancellationToken) =>
            {
                var machineProducts = await _nayax.GetMachineProductsAsync(machineId, machineCancellationToken);
                foreach (var machineProduct in machineProducts)
                {
                    if (machineProduct.NayaxProductID is not long productId)
                        continue;

                    var current = (machineProduct.PAR - machineProduct.MissingStockByMDB) ?? 0;
                    var target = machineProduct.PAR ?? 0;
                    var toPick = Math.Max(0, machineProduct.MissingStockByMDB ?? 0);

                    cellsByProductAndMachine.AddOrUpdate(
                        (productId, machineId),
                        _ => new PickListCell(current, target, toPick),
                        (_, existing) => existing.Add(current, target, toPick));
                }
            });

        if (cellsByProductAndMachine.IsEmpty)
            return new PickListResult(Array.Empty<PickListProduct>());

        var productIds = cellsByProductAndMachine.Keys.Select(key => key.ProductId).Distinct().ToList();
        var storageProducts = await _storage.GetStorageProductsAsync(productIds, cancellationToken);

        var products = cellsByProductAndMachine
            .GroupBy(cell => cell.Key.ProductId)
            .Where(group => storageProducts.ContainsKey(group.Key))
            .Select(group =>
            {
                var storageProduct = storageProducts[group.Key];
                var machineQuantities = group
                    .OrderBy(cell => cell.Key.MachineId)
                    .Select(cell => new PickListMachineQuantity(
                        cell.Key.MachineId, cell.Value.CurrentQuantity, cell.Value.TargetQuantity, cell.Value.QuantityToPick))
                    .ToList();
                var totalQuantityToPick = machineQuantities.Sum(machineQuantity => machineQuantity.QuantityToPick);
                var storageShortageQuantity = Math.Max(0, totalQuantityToPick - storageProduct.QuantityInStock);

                return new PickListProduct(
                    group.Key,
                    storageProduct.ProductName,
                    storageProduct.QuantityInStock,
                    totalQuantityToPick,
                    storageShortageQuantity,
                    machineQuantities);
            })
            .OrderBy(product => product.ProductName, StringComparer.Ordinal)
            .ToList();

        return new PickListResult(products);
    }

    /// <summary>
    /// The accumulated current/target/pick figures for one product on one machine, summed across
    /// however many MDB slots that product occupies there - the same duplicate-mapping aggregation
    /// <see cref="CalculateReorderNeeds"/> already applies within a single machine.
    /// </summary>
    private readonly record struct PickListCell(int CurrentQuantity, int TargetQuantity, int QuantityToPick)
    {
        public PickListCell Add(int current, int target, int toPick) =>
            new(CurrentQuantity + current, TargetQuantity + target, QuantityToPick + toPick);
    }
}
