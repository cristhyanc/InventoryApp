using System.Text.Json;
using Inventory.Application.Costing;
using Inventory.Domain.Costing;
using Xunit;

namespace InventoryApi.Tests.DTOs;

/// <summary>
/// Locks the <c>api/admin/inventory-cost-transition</c> wire contract across its move from
/// <c>InventoryApi/DTOs</c> into <c>Inventory.Application.Costing</c> (issue #298): the same keys in
/// the same order, the cost source as its persisted number, and a stored preview snapshot written
/// by the former API record still reading back.
/// </summary>
public class InventoryCostTransitionJsonContractTests
{
    private static readonly JsonSerializerOptions WebDefaults = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Batch_preview_serializes_with_the_established_keys_and_a_numeric_cost_source()
    {
        var product = new InventoryCostTransitionPreview(
            Guid.Empty, 10, "Snack", 4, [new(1, "Machine A", 3, "Nayax PAR - MissingStockByMDB")], 3, 7, 2m, 14m,
            new DateTime(2026, 9, 1, 2, 0, 0, DateTimeKind.Utc), InventoryCostBaselineSource.ManualEstimated, 4, 0, "Note");
        var batch = new InventoryCostTransitionBatchPreview(
            Guid.Empty, product.CutoffAt, InventoryCostBaselineSource.ManualAuthoritative, [product], 1, 4, 3, 7, 14m);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(batch, WebDefaults));

        Assert.Equal(
            ["previewId", "cutoffAt", "costSource", "products", "productCount", "homeStockQuantity",
                "machineStockQuantity", "openingCostingQuantity", "inventoryValue"],
            document.RootElement.EnumerateObject().Select(x => x.Name));
        Assert.Equal(1, document.RootElement.GetProperty("costSource").GetInt32());
        var productElement = document.RootElement.GetProperty("products")[0];
        Assert.Equal(
            ["previewId", "productId", "productName", "homeStockQuantity", "machineStocks", "machineStockQuantity",
                "openingCostingQuantity", "averageUnitCost", "inventoryValue", "cutoffAt", "costSource",
                "legacyReplayedPhysicalQuantity", "legacyPhysicalDiscrepancy", "dataQualityNote"],
            productElement.EnumerateObject().Select(x => x.Name));
        Assert.Equal(2, productElement.GetProperty("costSource").GetInt32());
        Assert.Equal(
            ["machineId", "machineName", "stockQuantity", "source"],
            productElement.GetProperty("machineStocks")[0].EnumerateObject().Select(x => x.Name));
    }

    [Fact]
    public void Requests_bind_from_the_established_keys_and_numeric_cost_source()
    {
        var preview = JsonSerializer.Deserialize<InventoryCostTransitionPreviewRequest>(
            """{"productId":10,"averageUnitCost":1.25,"costSource":2}""", WebDefaults);
        var batch = JsonSerializer.Deserialize<InventoryCostTransitionBatchPreviewRequest>(
            """{"costSource":1}""", WebDefaults);
        var apply = JsonSerializer.Deserialize<ApplyInventoryCostTransitionRequest>(
            """{"previewId":"6f9619ff-8b86-d011-b42d-00cf4fc964ff","confirmed":true}""", WebDefaults);

        Assert.Equal(new InventoryCostTransitionPreviewRequest(10, 1.25m, InventoryCostBaselineSource.ManualEstimated), preview);
        Assert.Equal(new InventoryCostTransitionBatchPreviewRequest(InventoryCostBaselineSource.ManualAuthoritative), batch);
        Assert.Equal(new ApplyInventoryCostTransitionRequest(Guid.Parse("6f9619ff-8b86-d011-b42d-00cf4fc964ff"), true), apply);
    }

    [Fact]
    public void A_stored_snapshot_written_by_the_former_api_record_still_reads_back()
    {
        // The draft SnapshotJson shape the removed InventoryCostTransitionService wrote with
        // JsonSerializer's default (PascalCase) options; an unexpired draft stored before the
        // deployment must still apply.
        const string stored = """
            {"PreviewId":"6f9619ff-8b86-d011-b42d-00cf4fc964ff","ProductId":10,"ProductName":"Snack","HomeStockQuantity":4,
             "MachineStocks":[{"MachineId":1,"MachineName":"Machine A","StockQuantity":3,"Source":"Nayax PAR - MissingStockByMDB"}],
             "MachineStockQuantity":3,"OpeningCostingQuantity":7,"AverageUnitCost":2,"InventoryValue":14,
             "CutoffAt":"2026-09-01T02:00:00Z","CostSource":1,"LegacyReplayedPhysicalQuantity":4,
             "LegacyPhysicalDiscrepancy":0,"DataQualityNote":"Note"}
            """;

        var preview = JsonSerializer.Deserialize<InventoryCostTransitionPreview>(stored)!;

        Assert.Equal(10, preview.ProductId);
        Assert.Equal(InventoryCostBaselineSource.ManualAuthoritative, preview.CostSource);
        Assert.Equal(3, Assert.Single(preview.MachineStocks).StockQuantity);
        Assert.Equal(14m, preview.InventoryValue);
        Assert.Equal(new DateTime(2026, 9, 1, 2, 0, 0, DateTimeKind.Utc), preview.CutoffAt);
    }
}
