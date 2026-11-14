using System.Globalization;
using System.Text.Json;
using Inventory.Application.Nayax;
using Inventory.Application.Time;
using Inventory.Domain.Costing;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Providers;

namespace Inventory.Application.Costing;

/// <summary>
/// Builds inventory-cost transition previews for the four transition use cases (issue #298),
/// unchanged from the former <c>InventoryCostTransitionService</c>: it reads every Nayax machine's
/// slots through the existing <see cref="INayaxLynxClient"/> port, fixes the cutoff after that read,
/// replays the legacy physical movements up to the cutoff through the store, and leaves every
/// calculation to <see cref="InventoryCostTransitionPolicy"/>.
///
/// <para><b>A slot names a product through the provider link</b> (issue #1256). The slot's
/// <c>NayaxProductID</c> is the Lynx contract's "unique identifier of the product in the Nayax
/// system" (<c>GET /v1/machines/{MachineID}/machineProducts</c>, <c>integer&lt;int64&gt;</c>,
/// nullable), so it is resolved to a local product id through <see cref="INayaxProductLinkStore"/> -
/// the same lookup <c>ListMachineProducts</c> and <c>GetSiteProducts</c> use - instead of being
/// compared with <c>Product.Id</c>. Comparing them only ever worked while the local key happened to
/// be the Nayax id; since issue #735 it is a coincidence, and here it silently decided a costing
/// baseline: a product whose ids differ lost its machine stock, and one whose Nayax id equalled
/// another product's local key gained that product's units. A slot this business holds no link for
/// is an unknown product and is counted for nobody.</para>
/// </summary>
internal sealed class InventoryCostTransitionPreviewBuilder
{
    private readonly IInventoryCostTransitionStore _store;
    private readonly INayaxLynxClient _nayax;
    private readonly INayaxProductLinkStore _productLinks;
    private readonly IClock _clock;

    public InventoryCostTransitionPreviewBuilder(
        IInventoryCostTransitionStore store,
        INayaxLynxClient nayax,
        INayaxProductLinkStore productLinks,
        IClock clock)
    {
        _store = store;
        _nayax = nayax;
        _productLinks = productLinks;
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
        var machineStocks = await ReadMachineStocksAsync(products, cancellationToken);
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

    /// <summary>
    /// Takes the products rather than their ids because a refused slot names the product (issue
    /// #833), and this is where the name the preview already holds reaches the policy.
    /// </summary>
    private async Task<Dictionary<long, List<InventoryCostTransitionMachineStockDto>>> ReadMachineStocksAsync(
        IReadOnlyCollection<InventoryCostTransitionProduct> products,
        CancellationToken cancellationToken)
    {
        var productIds = products.Select(x => x.Id).ToList();
        var productIdSet = productIds.ToHashSet();
        var results = productIds.ToDictionary(x => x, _ => new List<InventoryCostTransitionMachineStockDto>());
        // Fresh, never the cached list or the cached selections (issues #840 and #842): the preview
        // and the apply both record an opening machine stock per machine, which becomes a costing
        // baseline, so the fleet they are built over - and the quantities in it - have to be as they
        // are now.
        var machines = (await _nayax.GetMachinesFreshAsync(cancellationToken))
            .OrderBy(x => x.MachineName)
            .ThenBy(x => x.MachineID)
            .ToList();
        var snapshots = await Task.WhenAll(machines.Select(async machine =>
        {
            var slots = await _nayax.GetMachineProductsFreshAsync(machine.MachineID, cancellationToken);
            return (Machine: machine, Slots: slots.Where(x => x.NayaxProductID.HasValue).ToList());
        }));
        // Read after the Nayax fan-out has completed, never beside it: the lookup lands on the
        // request's own scoped AppDbContext, which supports one operation at a time, and the remote
        // reads above resolve the business's credentials through that same context (issue #667).
        var localProductIdByNayaxId = await _productLinks.GetLocalProductIdsAsync(
            snapshots
                .SelectMany(snapshot => snapshot.Slots)
                .Select(slot => slot.NayaxProductID!.Value)
                .Distinct()
                .ToList(),
            cancellationToken);
        foreach (var snapshot in snapshots)
        {
            var machineLabel = snapshot.Machine.MachineName ?? snapshot.Machine.MachineID.ToString(CultureInfo.CurrentCulture);
            // Each slot grouped under the local product it names. A slot this business holds no link
            // for, or one naming a product outside this preview, is counted for nobody - and in
            // particular is not offered to the policy, so another product's incomplete slot can
            // never refuse this preview.
            var slotsByProduct = snapshot.Slots
                .Select(slot => (
                    Slot: slot,
                    LocalProductId: localProductIdByNayaxId.TryGetValue(slot.NayaxProductID!.Value, out var localProductId)
                        ? localProductId
                        : (long?)null))
                .Where(pair => pair.LocalProductId is long id && productIdSet.Contains(id))
                .GroupBy(pair => pair.LocalProductId!.Value, pair => pair.Slot)
                .ToDictionary(group => group.Key, group => group.ToList());
            foreach (var product in products)
            {
                var productId = product.Id;
                var quantity = InventoryCostTransitionPolicy.MachineQuantity(
                    productId,
                    machineLabel,
                    (slotsByProduct.GetValueOrDefault(productId) ?? [])
                        .Select(x => new InventoryCostTransitionSlot(x.PAR, x.MissingStockByMDB)),
                    product.Name);
                results[productId].Add(new(
                    snapshot.Machine.MachineID,
                    snapshot.Machine.MachineName ?? $"Machine {snapshot.Machine.MachineID}",
                    quantity,
                    InventoryCostTransitionPolicy.MachineStockSource));
            }
        }
        return results;
    }

    /// <summary>
    /// Whether this product's machine stock could be looked up at all (issue #1256): a slot is
    /// matched to a product through the product's recorded provider id, so a product carrying none -
    /// or text the one Nayax conversion could not have written, which is not a Nayax id - matches no
    /// slot in any machine, and its preview says so instead of reporting an unmeasured zero.
    ///
    /// <para>The slot-side lookup cannot answer this on its own: a linked product that is in no
    /// machine and an unlinked product both resolve to no slot.</para>
    /// </summary>
    private static bool MachineStockResolved(InventoryCostTransitionProduct product) =>
        NayaxExternalId.TryParse(product.ExternalProductId, out _);

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
            replayedPhysical,
            MachineStockResolved(product));
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
