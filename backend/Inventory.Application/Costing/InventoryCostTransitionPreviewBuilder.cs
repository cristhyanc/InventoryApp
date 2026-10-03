using System.Globalization;
using System.Text.Json;
using Inventory.Application.Nayax;
using Inventory.Application.Time;
using Inventory.Domain.Costing;
using Inventory.Domain.Exceptions;

namespace Inventory.Application.Costing;

/// <summary>
/// Builds inventory-cost transition previews for the four transition use cases (issue #298),
/// unchanged from the former <c>InventoryCostTransitionService</c>: it reads every Nayax machine's
/// slots through the existing <see cref="INayaxLynxClient"/> port, fixes the cutoff after that read,
/// replays the legacy physical movements up to the cutoff through the store, and leaves every
/// calculation to <see cref="InventoryCostTransitionPolicy"/>.
/// </summary>
internal sealed class InventoryCostTransitionPreviewBuilder
{
    private readonly IInventoryCostTransitionStore _store;
    private readonly INayaxLynxClient _nayax;
    private readonly IClock _clock;

    public InventoryCostTransitionPreviewBuilder(IInventoryCostTransitionStore store, INayaxLynxClient nayax, IClock clock)
    {
        _store = store;
        _nayax = nayax;
        _clock = clock;
    }

    /// <summary>
    /// One preview per product, in the given order, all sharing <paramref name="previewId"/> and one
    /// cutoff. <paramref name="averageUnitCost"/> chooses each product's opening average unit cost.
    /// </summary>
    public async Task<(DateTime CutoffAt, IReadOnlyList<InventoryCostTransitionPreview> Products)> BuildAsync(
        Guid previewId,
        IReadOnlyList<InventoryCostTransitionProduct> products,
        Func<InventoryCostTransitionProduct, decimal> averageUnitCost,
        InventoryCostBaselineSource costSource,
        CancellationToken cancellationToken)
    {
        var ids = products.Select(x => x.Id).ToArray();
        var machineStocks = await ReadMachineStocksAsync(ids, cancellationToken);
        var cutoffAt = _clock.UtcNow;
        var replayedByProduct = await _store.SumPhysicalMovementsAsync(ids, cutoffAt, cancellationToken);
        var previews = products.Select(product => BuildProductPreview(
                previewId,
                product,
                machineStocks[product.Id],
                averageUnitCost(product),
                cutoffAt,
                costSource,
                replayedByProduct.GetValueOrDefault(product.Id)))
            .ToList();
        return (cutoffAt, previews);
    }

    public async Task<InventoryCostTransitionBatchPreview> BuildBatchAsync(
        Guid previewId,
        IReadOnlyList<InventoryCostTransitionProduct> products,
        InventoryCostBaselineSource costSource,
        CancellationToken cancellationToken)
    {
        var (cutoffAt, previews) = await BuildAsync(
            previewId, products, product => product.AverageUnitCost, costSource, cancellationToken);
        return new(
            previewId,
            cutoffAt,
            costSource,
            previews,
            previews.Count,
            previews.Sum(x => x.HomeStockQuantity),
            previews.Sum(x => x.MachineStockQuantity),
            previews.Sum(x => x.OpeningCostingQuantity),
            previews.Sum(x => x.InventoryValue));
    }

    public NewInventoryCostTransitionDraft NewDraft<T>(Guid previewId, long productId, T snapshot)
    {
        var now = _clock.UtcNow;
        return new(previewId, productId, JsonSerializer.Serialize(snapshot), now, now.Add(InventoryCostTransitionPolicy.PreviewLifetime));
    }

    public static T ReadSnapshot<T>(StoredInventoryCostTransitionDraft draft) =>
        JsonSerializer.Deserialize<T>(draft.SnapshotJson)
        ?? throw new DomainValidationException("The transition preview could not be read.");

    public static InventoryCostTransitionState ToState(InventoryCostTransitionPreview preview) =>
        new(
            preview.ProductId,
            preview.ProductName,
            preview.HomeStockQuantity,
            preview.MachineStocks.Select(x => new InventoryCostTransitionMachineQuantity(x.MachineId, x.StockQuantity)).ToList(),
            preview.MachineStockQuantity,
            preview.OpeningCostingQuantity,
            preview.AverageUnitCost,
            preview.InventoryValue,
            preview.LegacyReplayedPhysicalQuantity);

    private async Task<Dictionary<long, List<InventoryCostTransitionMachineStockDto>>> ReadMachineStocksAsync(
        IReadOnlyCollection<long> productIds,
        CancellationToken cancellationToken)
    {
        var productIdSet = productIds.ToHashSet();
        var results = productIds.ToDictionary(x => x, _ => new List<InventoryCostTransitionMachineStockDto>());
        var machines = (await _nayax.GetMachinesAsync(cancellationToken))
            .OrderBy(x => x.MachineName)
            .ThenBy(x => x.MachineID)
            .ToList();
        var snapshots = await Task.WhenAll(machines.Select(async machine =>
        {
            var slots = await _nayax.GetMachineProductsAsync(machine.MachineID, cancellationToken);
            return (Machine: machine, Slots: slots.Where(x =>
                x.NayaxProductID.HasValue && productIdSet.Contains(x.NayaxProductID.Value)).ToList());
        }));
        foreach (var snapshot in snapshots)
        {
            var machineLabel = snapshot.Machine.MachineName ?? snapshot.Machine.MachineID.ToString(CultureInfo.CurrentCulture);
            foreach (var productId in productIds)
            {
                var quantity = InventoryCostTransitionPolicy.MachineQuantity(
                    productId,
                    machineLabel,
                    snapshot.Slots
                        .Where(x => x.NayaxProductID == productId)
                        .Select(x => new InventoryCostTransitionSlot(x.PAR, x.MissingStockByMDB)));
                results[productId].Add(new(
                    snapshot.Machine.MachineID,
                    snapshot.Machine.MachineName ?? $"Machine {snapshot.Machine.MachineID}",
                    quantity,
                    InventoryCostTransitionPolicy.MachineStockSource));
            }
        }
        return results;
    }

    private static InventoryCostTransitionPreview BuildProductPreview(
        Guid previewId,
        InventoryCostTransitionProduct product,
        IReadOnlyList<InventoryCostTransitionMachineStockDto> machineStocks,
        decimal averageUnitCost,
        DateTime cutoffAt,
        InventoryCostBaselineSource costSource,
        int replayedPhysical)
    {
        var opening = InventoryCostTransitionPolicy.CalculateOpening(
            product.QuantityInStock,
            machineStocks.Select(x => x.StockQuantity),
            averageUnitCost,
            replayedPhysical);
        return new(
            previewId,
            product.Id,
            product.Name,
            product.QuantityInStock,
            machineStocks,
            opening.MachineStockQuantity,
            opening.OpeningCostingQuantity,
            averageUnitCost,
            opening.InventoryValue,
            cutoffAt,
            costSource,
            replayedPhysical,
            opening.LegacyPhysicalDiscrepancy,
            opening.DataQualityNote);
    }
}
