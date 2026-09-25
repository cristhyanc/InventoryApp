using Inventory.Application.Exceptions;
using InventoryApi.Data;
using InventoryApi.DTOs;
using InventoryApi.Integrations.Nayax;
using InventoryApi.Models;
using InventoryApi.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace InventoryApi.Services;

/// <summary>
/// Every deliberate check below throws <see cref="DomainValidationException"/>, whose message is
/// written for the caller: <c>DomainExceptionHandler</c> returns it verbatim as the 400
/// ProblemDetails this service's controller used to build in its own catch block (issue #59).
/// A failure that is not a reviewed, caller-safe validation message must keep throwing an ordinary
/// framework exception so it reaches <c>GlobalExceptionHandler</c> as a logged, generic 500.
/// </summary>
public sealed class InventoryCostTransitionService : IInventoryCostTransitionService
{
    private readonly AppDbContext _db;
    private readonly INayaxLynxClient _nayax;
    private readonly IInventoryCostRebuildService _rebuild;

    public InventoryCostTransitionService(
        AppDbContext db,
        INayaxLynxClient nayax,
        IInventoryCostRebuildService rebuild)
    {
        _db = db;
        _nayax = nayax;
        _rebuild = rebuild;
    }

    public async Task<InventoryCostTransitionPreview> PreviewAsync(
        InventoryCostTransitionPreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.AverageUnitCost < 0)
            throw new DomainValidationException("The opening average unit cost cannot be negative.");
        if (!Enum.IsDefined(request.CostSource))
            throw new DomainValidationException("Select whether the opening cost is authoritative or estimated.");
        if (await _db.InventoryCostTransitionBaselines.AnyAsync(x => x.ProductId == request.ProductId, cancellationToken))
            throw new DomainValidationException("This product already has an inventory-cost transition baseline.");

        var preview = await BuildPreviewAsync(request, cancellationToken);
        var draft = new InventoryCostTransitionPreviewDraft
        {
            Id = preview.PreviewId,
            ProductId = preview.ProductId,
            SnapshotJson = JsonSerializer.Serialize(preview),
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(30)
        };
        _db.InventoryCostTransitionPreviewDrafts.Add(draft);
        await _db.SaveChangesAsync(cancellationToken);
        return preview;
    }

    private async Task<InventoryCostTransitionPreview> BuildPreviewAsync(
        InventoryCostTransitionPreviewRequest request,
        CancellationToken cancellationToken)
    {
        var product = await _db.Products.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == request.ProductId, cancellationToken)
            ?? throw new DomainValidationException($"Product {request.ProductId} does not exist.");
        var machineStocksByProduct = await ReadMachineStocksAsync(new[] { product.Id }, cancellationToken);
        var cutoffAt = DateTime.UtcNow;
        var replayedPhysical = await _db.StockAdjustments.AsNoTracking()
            .Where(x => x.ProductId == product.Id && x.EffectiveAt <= cutoffAt)
            .SumAsync(x => x.QuantityChange, cancellationToken);
        return BuildProductPreview(
            Guid.NewGuid(),
            product,
            machineStocksByProduct[product.Id],
            request.AverageUnitCost,
            cutoffAt,
            request.CostSource,
            replayedPhysical);
    }

    public async Task<InventoryCostTransitionPreview> ApplyAsync(
        ApplyInventoryCostTransitionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!request.Confirmed)
            throw new DomainValidationException("Explicit confirmation is required to save the transition baseline.");

        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(cancellationToken)
            : null;
        var draft = await _db.InventoryCostTransitionPreviewDrafts
            .SingleOrDefaultAsync(x => x.Id == request.PreviewId && x.ProductId > 0, cancellationToken)
            ?? throw new DomainValidationException("The transition preview does not exist. Run the preview again.");
        ValidateDraft(draft);
        var expected = Deserialize<InventoryCostTransitionPreview>(draft.SnapshotJson);
        var current = await BuildPreviewAsync(
            new(expected.ProductId, expected.AverageUnitCost, expected.CostSource),
            cancellationToken);
        ValidateUnchanged(expected, current);

        _db.InventoryCostTransitionBaselines.Add(ToBaseline(expected));
        draft.AppliedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        await _rebuild.RebuildAsync(expected.ProductId, expected.CutoffAt, cancellationToken: cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        return expected;
    }

    public async Task<InventoryCostTransitionBatchPreview> PreviewAllAsync(
        InventoryCostTransitionBatchPreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(request.CostSource))
            throw new DomainValidationException("Select whether the opening costs are authoritative or estimated.");
        var baselineProductIds = await _db.InventoryCostTransitionBaselines.AsNoTracking()
            .Select(x => x.ProductId)
            .ToListAsync(cancellationToken);
        var products = await _db.Products.AsNoTracking()
            .Where(x => !baselineProductIds.Contains(x.Id))
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);
        if (products.Count == 0)
            throw new DomainValidationException("All products already have an inventory-cost transition baseline.");
        if (products.Any(x => x.AverageUnitCost < 0))
            throw new DomainValidationException("One or more products have a negative current average unit cost.");

        var preview = await BuildBatchPreviewAsync(products, request.CostSource, Guid.NewGuid(), cancellationToken);
        _db.InventoryCostTransitionPreviewDrafts.Add(new InventoryCostTransitionPreviewDraft
        {
            Id = preview.PreviewId,
            ProductId = 0,
            SnapshotJson = JsonSerializer.Serialize(preview),
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(30)
        });
        await _db.SaveChangesAsync(cancellationToken);
        return preview;
    }

    public async Task<InventoryCostTransitionBatchPreview> ApplyAllAsync(
        ApplyInventoryCostTransitionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!request.Confirmed)
            throw new DomainValidationException("Explicit confirmation is required to save all transition baselines.");

        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(cancellationToken)
            : null;
        var draft = await _db.InventoryCostTransitionPreviewDrafts
            .SingleOrDefaultAsync(x => x.Id == request.PreviewId && x.ProductId == 0, cancellationToken)
            ?? throw new DomainValidationException("The all-products transition preview does not exist. Run the preview again.");
        ValidateDraft(draft);
        var expected = Deserialize<InventoryCostTransitionBatchPreview>(draft.SnapshotJson);
        var productIds = expected.Products.Select(x => x.ProductId).ToList();
        if (await _db.InventoryCostTransitionBaselines.AnyAsync(x => productIds.Contains(x.ProductId), cancellationToken))
            throw new DomainValidationException("One or more products received a baseline after this preview. Run the preview again.");
        var products = await _db.Products.AsNoTracking()
            .Where(x => productIds.Contains(x.Id))
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);
        if (products.Count != productIds.Count)
            throw new DomainValidationException("One or more previewed products no longer exist.");
        var current = await BuildBatchPreviewAsync(products, expected.CostSource, Guid.NewGuid(), cancellationToken);
        ValidateBatchUnchanged(expected, current);

        foreach (var product in expected.Products)
            _db.InventoryCostTransitionBaselines.Add(ToBaseline(product));
        draft.AppliedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        foreach (var product in expected.Products)
            await _rebuild.RebuildAsync(product.ProductId, product.CutoffAt, cancellationToken: cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
        return expected;
    }

    private async Task<InventoryCostTransitionBatchPreview> BuildBatchPreviewAsync(
        IReadOnlyCollection<Product> products,
        InventoryCostBaselineSource costSource,
        Guid previewId,
        CancellationToken cancellationToken)
    {
        var ids = products.Select(x => x.Id).ToArray();
        var machineStocks = await ReadMachineStocksAsync(ids, cancellationToken);
        var cutoffAt = DateTime.UtcNow;
        var adjustments = await _db.StockAdjustments.AsNoTracking()
            .Where(x => ids.Contains(x.ProductId) && x.EffectiveAt <= cutoffAt)
            .Select(x => new { x.ProductId, x.QuantityChange })
            .ToListAsync(cancellationToken);
        var replayedByProduct = adjustments
            .GroupBy(x => x.ProductId)
            .ToDictionary(group => group.Key, group => group.Sum(x => x.QuantityChange));
        var previews = products.Select(product => BuildProductPreview(
                previewId,
                product,
                machineStocks[product.Id],
                product.AverageUnitCost,
                cutoffAt,
                costSource,
                replayedByProduct.GetValueOrDefault(product.Id)))
            .ToList();
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
            foreach (var productId in productIds)
            {
                var quantity = 0;
                foreach (var slot in snapshot.Slots.Where(x => x.NayaxProductID == productId))
                {
                    if (!slot.PAR.HasValue || !slot.MissingStockByMDB.HasValue)
                        throw new DomainValidationException(
                            $"Nayax stock is incomplete for product {productId} in machine {snapshot.Machine.MachineName ?? snapshot.Machine.MachineID.ToString()}: PAR and MissingStockByMDB are required.");
                    var slotQuantity = slot.PAR.Value - slot.MissingStockByMDB.Value;
                    if (slotQuantity < 0 || slotQuantity > slot.PAR.Value)
                        throw new DomainValidationException(
                            $"Nayax stock is invalid for product {productId} in machine {snapshot.Machine.MachineName ?? snapshot.Machine.MachineID.ToString()}.");
                    quantity = checked(quantity + slotQuantity);
                }
                results[productId].Add(new(
                    snapshot.Machine.MachineID,
                    snapshot.Machine.MachineName ?? $"Machine {snapshot.Machine.MachineID}",
                    quantity,
                    "Nayax PAR - MissingStockByMDB"));
            }
        }
        return results;
    }

    private static InventoryCostTransitionPreview BuildProductPreview(
        Guid previewId,
        Product product,
        IReadOnlyList<InventoryCostTransitionMachineStockDto> machineStocks,
        decimal averageUnitCost,
        DateTime cutoffAt,
        InventoryCostBaselineSource costSource,
        int replayedPhysical)
    {
        var machineQuantity = machineStocks.Sum(x => x.StockQuantity);
        var costingQuantity = checked(product.QuantityInStock + machineQuantity);
        var discrepancy = product.QuantityInStock - replayedPhysical;
        var note = discrepancy == 0
            ? "Legacy physical movement history reconciled at transition."
            : $"Legacy physical movement history replayed to {replayedPhysical}, while verified home stock was {product.QuantityInStock}; discrepancy {discrepancy:+#;-#;0} was retired at cutover without altering legacy movements.";
        return new(
            previewId,
            product.Id,
            product.Name,
            product.QuantityInStock,
            machineStocks,
            machineQuantity,
            costingQuantity,
            averageUnitCost,
            costingQuantity * averageUnitCost,
            cutoffAt,
            costSource,
            replayedPhysical,
            discrepancy,
            note);
    }

    private static InventoryCostTransitionBaseline ToBaseline(InventoryCostTransitionPreview preview) =>
        new()
        {
            ProductId = preview.ProductId,
            CutoffAt = preview.CutoffAt,
            HomeStockQuantity = preview.HomeStockQuantity,
            MachineStockQuantity = preview.MachineStockQuantity,
            OpeningCostingQuantity = preview.OpeningCostingQuantity,
            AverageUnitCost = preview.AverageUnitCost,
            InventoryValue = preview.InventoryValue,
            CostSource = preview.CostSource,
            LegacyReplayedPhysicalQuantity = preview.LegacyReplayedPhysicalQuantity,
            LegacyPhysicalDiscrepancy = preview.LegacyPhysicalDiscrepancy,
            DataQualityNote = preview.DataQualityNote,
            MachineStocks = preview.MachineStocks.Select(x => new InventoryCostTransitionMachineStock
            {
                MachineId = x.MachineId,
                MachineName = x.MachineName,
                StockQuantity = x.StockQuantity,
                Source = x.Source
            }).ToList()
        };

    private static void ValidateDraft(InventoryCostTransitionPreviewDraft draft)
    {
        if (draft.AppliedAt.HasValue)
            throw new DomainValidationException("This transition preview has already been applied.");
        if (draft.ExpiresAt < DateTime.UtcNow)
            throw new DomainValidationException("The transition preview expired. Run the preview again.");
    }

    private static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json)
        ?? throw new DomainValidationException("The transition preview could not be read.");

    private static void ValidateUnchanged(
        InventoryCostTransitionPreview expected,
        InventoryCostTransitionPreview current)
    {
        var expectedMachines = expected.MachineStocks.OrderBy(x => x.MachineId).ToArray();
        var currentMachines = current.MachineStocks.OrderBy(x => x.MachineId).ToArray();
        var machineStocksMatch = expectedMachines.Length == currentMachines.Length &&
            expectedMachines.Zip(currentMachines).All(pair =>
                pair.First.MachineId == pair.Second.MachineId &&
                pair.First.StockQuantity == pair.Second.StockQuantity);
        var expectedMachineQuantity = expectedMachines.Sum(x => x.StockQuantity);

        if (expected.HomeStockQuantity != current.HomeStockQuantity ||
            expected.LegacyReplayedPhysicalQuantity != current.LegacyReplayedPhysicalQuantity ||
            !machineStocksMatch)
            throw new DomainValidationException(
                "Inventory data changed after the preview. Run the preview again before confirming.");
        if (expected.MachineStockQuantity != expectedMachineQuantity ||
            expected.OpeningCostingQuantity != expected.HomeStockQuantity + expected.MachineStockQuantity ||
            expected.InventoryValue != expected.OpeningCostingQuantity * expected.AverageUnitCost)
            throw new DomainValidationException("The confirmed transition values do not match the preview calculation.");
    }

    private static void ValidateBatchUnchanged(
        InventoryCostTransitionBatchPreview expected,
        InventoryCostTransitionBatchPreview current)
    {
        if (expected.Products.Count != current.Products.Count)
            throw new DomainValidationException("The eligible product list changed after the preview. Run it again.");
        var currentByProduct = current.Products.ToDictionary(x => x.ProductId);
        foreach (var product in expected.Products)
        {
            if (!currentByProduct.TryGetValue(product.ProductId, out var currentProduct))
                throw new DomainValidationException("The eligible product list changed after the preview. Run it again.");
            if (product.AverageUnitCost != currentProduct.AverageUnitCost)
                throw new DomainValidationException(
                    $"The average unit cost for {product.ProductName} changed after the preview. Run it again.");
            ValidateUnchanged(product, currentProduct);
        }
    }
}
