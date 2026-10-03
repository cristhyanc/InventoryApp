using System.Text.Json;
using Inventory.Application.Products;
using InventoryApi.Adapters.Mapping;
using InventoryApi.DTOs;
using InventoryApi.Models;
using Xunit;

namespace InventoryApi.Tests.DTOs;

/// <summary>
/// Locks the wire contract of the "/api/products" endpoints now that
/// <see cref="ProductResponse"/> replaced the EF <c>Product</c> entity the controller used to
/// serialize directly (issue #303).
///
/// The reference value is the entity itself: each test builds the <c>Product</c> the retired
/// delegator would have returned for the same Application record and compares the serialized
/// bytes, so a key that is renamed, reordered, added, dropped, or given a different value fails
/// here rather than in a client. The explicit key list is asserted as well, so a future change to
/// <em>both</em> sides at once still has to be a conscious one.
/// </summary>
public class ProductJsonContractTests
{
    private static readonly JsonSerializerOptions WebDefaults = new(JsonSerializerDefaults.Web);

    private static ProductRecord FullyPopulatedRecord() => new()
    {
        Id = 7,
        Name = "Coke",
        Sku = "SKU-1",
        Description = "A can",
        UnitPrice = 3.50m,
        AverageUnitCost = 1.25m,
        CostingQuantity = 7,
        InventoryValue = 8.75m,
        QuantityInStock = 4,
        LowStockThreshold = 10,
        RestockTo = 20,
        Unit = "can",
        IsActive = true,
        CreatedAt = new DateTime(2026, 1, 1, 8, 30, 0, DateTimeKind.Utc),
        UpdatedAt = new DateTime(2026, 2, 2, 9, 45, 0, DateTimeKind.Utc),
        CategoryId = 3,
        Category = new ProductCategoryRecord(3, "Drinks", "Cold"),
        SupplierId = 4,
        Supplier = new ProductSupplierRecord(4, "Acme", "Pat", "123", "a@b.c", "1 Road"),
        StockAdjustments =
        [
            new ProductStockAdjustmentRecord(
                11,
                7,
                12,
                4,
                4,
                1.25m,
                5m,
                7,
                1.25m,
                8.75m,
                Inventory.Domain.Stock.StockAdjustmentReason.Restock,
                Inventory.Domain.Stock.StockAdjustmentSource.Nayax,
                13,
                "Initial",
                new DateTime(2026, 6, 1),
                new DateTime(2026, 1, 1, 8, 30, 0, DateTimeKind.Utc),
                new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc)),
        ],
        MachineReplenishmentNeed = 3,
        OnOrderQuantity = 2m,
    };

    /// <summary>
    /// The <c>Product</c> the retired <c>ProductService</c> returned for <paramref name="record"/>,
    /// rebuilt here as the reference wire shape. The machine-slot fields stay at their defaults,
    /// exactly as they did on every "/api/products" response, because only the machine-product view
    /// (issue #302) ever overlays them.
    /// </summary>
    private static Product EquivalentEntity(ProductRecord record) => new()
    {
        Id = record.Id,
        Name = record.Name,
        Sku = record.Sku,
        Description = record.Description,
        UnitPrice = record.UnitPrice,
        AverageUnitCost = record.AverageUnitCost,
        CostingQuantity = record.CostingQuantity,
        InventoryValue = record.InventoryValue,
        QuantityInStock = record.QuantityInStock,
        LowStockThreshold = record.LowStockThreshold,
        RestockTo = record.RestockTo,
        Unit = record.Unit,
        IsActive = record.IsActive,
        CreatedAt = record.CreatedAt,
        UpdatedAt = record.UpdatedAt,
        CategoryId = record.CategoryId,
        Category = record.Category is null
            ? null
            : new Category
            {
                Id = record.Category.Id,
                Name = record.Category.Name,
                Description = record.Category.Description,
            },
        SupplierId = record.SupplierId,
        Supplier = record.Supplier is null
            ? null
            : new Supplier
            {
                Id = record.Supplier.Id,
                Name = record.Supplier.Name,
                ContactName = record.Supplier.ContactName,
                Phone = record.Supplier.Phone,
                Email = record.Supplier.Email,
                Address = record.Supplier.Address,
            },
        StockAdjustments = record.StockAdjustments.Select(adjustment => new StockAdjustment
        {
            Id = adjustment.Id,
            ProductId = adjustment.ProductId,
            ReceiptItemId = adjustment.ReceiptItemId,
            QuantityChange = adjustment.QuantityChange,
            QuantityAfter = adjustment.QuantityAfter,
            UnitCost = adjustment.UnitCost,
            TotalCost = adjustment.TotalCost,
            CostingQuantityAfter = adjustment.CostingQuantityAfter,
            AverageUnitCostAfter = adjustment.AverageUnitCostAfter,
            InventoryValueAfter = adjustment.InventoryValueAfter,
            Reason = (StockAdjustmentReason)adjustment.Reason,
            Source = (StockAdjustmentSource)adjustment.Source,
            MachineId = adjustment.MachineId,
            Notes = adjustment.Notes,
            EatBefore = adjustment.EatBefore,
            CreatedAt = adjustment.CreatedAt,
            EffectiveAt = adjustment.EffectiveAt,
        }).ToList(),
        MachineReplenishmentNeed = record.MachineReplenishmentNeed,
        OnOrderQuantity = record.OnOrderQuantity,
    };

    [Fact]
    public void Response_serializes_byte_for_byte_like_the_entity_it_replaced()
    {
        var record = FullyPopulatedRecord();

        var response = JsonSerializer.Serialize(ProductRecordResponseMapper.ToResponse(record), WebDefaults);

        Assert.Equal(JsonSerializer.Serialize(EquivalentEntity(record), WebDefaults), response);
    }

    /// <summary>
    /// The sparse read path: no category, no supplier, no stock history, and none of the reorder
    /// inputs resolved - what <c>GET /api/products</c> and <c>GET /api/products/{id}</c> answer for
    /// a bare product. A nested object must still be emitted as explicit null, not omitted.
    /// </summary>
    [Fact]
    public void Response_serializes_byte_for_byte_like_the_entity_when_no_detail_is_loaded()
    {
        var record = new ProductRecord
        {
            Id = 1,
            Name = "Bare",
            UnitPrice = 0m,
            AverageUnitCost = 0m,
            QuantityInStock = 0,
            LowStockThreshold = 0,
            RestockTo = 0,
            IsActive = false,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        var response = JsonSerializer.Serialize(ProductRecordResponseMapper.ToResponse(record), WebDefaults);

        Assert.Equal(JsonSerializer.Serialize(EquivalentEntity(record), WebDefaults), response);
        Assert.Contains("\"category\":null", response, StringComparison.Ordinal);
        Assert.Contains("\"supplier\":null", response, StringComparison.Ordinal);
        Assert.Contains("\"stockAdjustments\":[]", response, StringComparison.Ordinal);
    }

    [Fact]
    public void Response_exposes_the_same_keys_in_the_same_order_the_entity_did()
    {
        var json = JsonSerializer.Serialize(
            ProductRecordResponseMapper.ToResponse(FullyPopulatedRecord()), WebDefaults);

        using var document = JsonDocument.Parse(json);
        Assert.Equal(
            new[]
            {
                "id", "name", "sku", "description", "unitPrice", "averageUnitCost", "costingQuantity",
                "inventoryValue", "machinePrice", "commissionValue", "suggestedNetValue",
                "suggestedPriceValue", "mdbCode", "maxStockInMachine", "machineReplenishmentNeed",
                "onOrderQuantity", "projectedStockForReorder", "quantityInStock", "lowStockThreshold",
                "restockTo", "needToOrder", "unit", "isActive", "lastEatBefore1", "lastEatBefore2",
                "createdAt", "updatedAt", "categoryId", "category", "supplierId", "supplier",
                "stockAdjustments", "isLowStock", "isReorderAlert",
            },
            document.RootElement.EnumerateObject().Select(property => property.Name));

        Assert.Equal(
            new[] { "id", "name", "description" },
            document.RootElement.GetProperty("category").EnumerateObject().Select(property => property.Name));

        Assert.Equal(
            new[] { "id", "name", "contactName", "phone", "email", "address" },
            document.RootElement.GetProperty("supplier").EnumerateObject().Select(property => property.Name));

        Assert.Equal(
            new[]
            {
                "id", "productId", "receiptItemId", "quantityChange", "quantityAfter", "unitCost",
                "totalCost", "costingQuantityAfter", "averageUnitCostAfter", "inventoryValueAfter",
                "reason", "source", "machineId", "notes", "eatBefore", "createdAt", "effectiveAt",
            },
            document.RootElement.GetProperty("stockAdjustments")[0].EnumerateObject()
                .Select(property => property.Name));
    }

    /// <summary>
    /// The derived reorder values clients read straight off the response: they must keep coming from
    /// the one authoritative <c>ProductReorderPolicy</c>, over the live machine need and outstanding
    /// supplier-order quantity the low-stock listing resolves, not be recomputed by the API.
    /// Projected stock 4 on hand + 2 on order - 3 needed by machines = 3, at or below the threshold
    /// of 10, so the reorder quantity is the restock target 20 plus the machines' need 3, net of the
    /// 4 on hand and the 2 already on order: 17 units.
    /// </summary>
    [Fact]
    public void Response_derives_the_reorder_values_from_the_resolved_reorder_inputs()
    {
        var json = JsonSerializer.Serialize(
            ProductRecordResponseMapper.ToResponse(FullyPopulatedRecord()), WebDefaults);

        using var document = JsonDocument.Parse(json);
        Assert.Equal(3m, document.RootElement.GetProperty("projectedStockForReorder").GetDecimal());
        Assert.Equal(17m, document.RootElement.GetProperty("needToOrder").GetDecimal());
        Assert.True(document.RootElement.GetProperty("isLowStock").GetBoolean());
        Assert.True(document.RootElement.GetProperty("isReorderAlert").GetBoolean());
    }

    [Fact]
    public void Response_omits_the_owning_business()
    {
        var json = JsonSerializer.Serialize(
            ProductRecordResponseMapper.ToResponse(FullyPopulatedRecord()), WebDefaults);

        Assert.DoesNotContain("businessId", json, StringComparison.OrdinalIgnoreCase);
    }
}
