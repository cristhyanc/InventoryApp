using System.Text.Json;
using Inventory.Application.Stock;
using InventoryApi.Adapters.Mapping;
using InventoryApi.DTOs;
using InventoryApi.Models;
using Xunit;
using DomainStock = Inventory.Domain.Stock;

namespace InventoryApi.Tests.DTOs;

/// <summary>
/// Locks the wire contract of the "/api/products/{productId}/stock" history and adjust endpoints
/// now that the API-owned <see cref="ProductStockAdjustmentResponse"/> replaced the
/// <c>InventoryApi.Models.StockAdjustment</c> entity <c>StockAdjustmentResponseMapper</c> used to
/// rebuild for them (issue #305).
///
/// The reference value is the entity itself: each test builds the <c>StockAdjustment</c> the former
/// mapper would have returned for the same Application record and compares the serialized bytes, so
/// a key that is renamed, reordered, added, dropped, or given a different value fails here rather
/// than in a client. The explicit key list is asserted as well, so a future change to <em>both</em>
/// sides at once still has to be a conscious one.
/// </summary>
public class StockAdjustmentResponseJsonContractTests
{
    private static readonly JsonSerializerOptions WebDefaults = new(JsonSerializerDefaults.Web);

    private static StockAdjustmentRecord FullyPopulatedRecord() => new(
        Id: 41,
        BusinessId: 2,
        ProductId: 7,
        ReceiptItemId: 13,
        QuantityChange: 12,
        QuantityAfter: 30,
        UnitCost: 1.25m,
        TotalCost: 15m,
        CostingQuantityAfter: 28,
        AverageUnitCostAfter: 1.30m,
        InventoryValueAfter: 36.40m,
        Reason: DomainStock.StockAdjustmentReason.Restock,
        Source: DomainStock.StockAdjustmentSource.Manual,
        MachineId: 9,
        Notes: "Weekly restock",
        EatBefore: new DateTime(2026, 6, 30),
        CreatedAt: new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc),
        EffectiveAt: new DateTime(2026, 3, 1, 9, 30, 0, DateTimeKind.Utc));

    /// <summary>
    /// The <c>StockAdjustment</c> the former <c>StockAdjustmentResponseMapper.ToStockAdjustment</c>
    /// built for <paramref name="record"/>, rebuilt here as the reference wire shape so the
    /// production code no longer has to construct an entity to describe its own contract.
    /// </summary>
    private static StockAdjustment EquivalentEntity(StockAdjustmentRecord record) => new()
    {
        BusinessId = record.BusinessId,
        Id = record.Id,
        ProductId = record.ProductId,
        ReceiptItemId = record.ReceiptItemId,
        QuantityChange = record.QuantityChange,
        QuantityAfter = record.QuantityAfter,
        UnitCost = record.UnitCost,
        TotalCost = record.TotalCost,
        CostingQuantityAfter = record.CostingQuantityAfter,
        AverageUnitCostAfter = record.AverageUnitCostAfter,
        InventoryValueAfter = record.InventoryValueAfter,
        Reason = (StockAdjustmentReason)record.Reason,
        Source = (StockAdjustmentSource)record.Source,
        MachineId = record.MachineId,
        Notes = record.Notes,
        EatBefore = record.EatBefore,
        CreatedAt = record.CreatedAt,
        EffectiveAt = record.EffectiveAt,
    };

    [Fact]
    public void Response_serializes_byte_for_byte_like_the_entity_it_replaced()
    {
        var record = FullyPopulatedRecord();

        var response = JsonSerializer.Serialize(StockAdjustmentResponseMapper.ToResponse(record), WebDefaults);

        Assert.Equal(JsonSerializer.Serialize(EquivalentEntity(record), WebDefaults), response);
    }

    /// <summary>
    /// A sale's cost-bearing movement before the ledger could cost it: every optional cost, the
    /// machine, the note and the eat-before date absent. The absent values must still be emitted as
    /// explicit nulls, not omitted, exactly as the entity emitted them.
    /// </summary>
    [Fact]
    public void Response_serializes_byte_for_byte_like_the_entity_when_every_optional_value_is_absent()
    {
        var record = FullyPopulatedRecord() with
        {
            ReceiptItemId = null,
            QuantityChange = -1,
            QuantityAfter = 29,
            UnitCost = null,
            TotalCost = null,
            CostingQuantityAfter = null,
            AverageUnitCostAfter = null,
            InventoryValueAfter = null,
            Reason = DomainStock.StockAdjustmentReason.Sale,
            MachineId = null,
            Notes = null,
            EatBefore = null,
        };

        var response = JsonSerializer.Serialize(StockAdjustmentResponseMapper.ToResponse(record), WebDefaults);

        Assert.Equal(JsonSerializer.Serialize(EquivalentEntity(record), WebDefaults), response);
        Assert.Contains("\"unitCost\":null", response, StringComparison.Ordinal);
        Assert.Contains("\"machineId\":null", response, StringComparison.Ordinal);
        Assert.Contains("\"eatBefore\":null", response, StringComparison.Ordinal);
    }

    [Fact]
    public void Response_exposes_the_same_keys_in_the_same_order_the_entity_did()
    {
        var json = JsonSerializer.Serialize(
            StockAdjustmentResponseMapper.ToResponse(FullyPopulatedRecord()), WebDefaults);

        using var document = JsonDocument.Parse(json);
        Assert.Equal(
            new[]
            {
                "id", "productId", "receiptItemId", "quantityChange", "quantityAfter", "unitCost",
                "totalCost", "costingQuantityAfter", "averageUnitCostAfter", "inventoryValueAfter",
                "reason", "source", "machineId", "notes", "eatBefore", "createdAt", "effectiveAt",
            },
            document.RootElement.EnumerateObject().Select(property => property.Name));
    }

    /// <summary>
    /// Every reason and source a movement can carry, serialized as the persisted numeric value a
    /// client switches on. This is the acceptance criterion that the stock-adjustment reason is
    /// serialized with the same values as before, asserted member by member rather than for one
    /// sample.
    /// </summary>
    [Theory]
    [InlineData(DomainStock.StockAdjustmentReason.Restock, 0)]
    [InlineData(DomainStock.StockAdjustmentReason.Sale, 1)]
    [InlineData(DomainStock.StockAdjustmentReason.Damaged, 2)]
    [InlineData(DomainStock.StockAdjustmentReason.Expired, 3)]
    [InlineData(DomainStock.StockAdjustmentReason.Correction, 4)]
    [InlineData(DomainStock.StockAdjustmentReason.MachineRefill, 5)]
    public void Response_serializes_each_reason_with_its_persisted_numeric_value(
        DomainStock.StockAdjustmentReason reason, int expected)
    {
        var record = FullyPopulatedRecord() with { Reason = reason };

        var json = JsonSerializer.Serialize(StockAdjustmentResponseMapper.ToResponse(record), WebDefaults);

        using var document = JsonDocument.Parse(json);
        Assert.Equal(expected, document.RootElement.GetProperty("reason").GetInt32());
    }

    [Theory]
    [InlineData(DomainStock.StockAdjustmentSource.Manual, 0)]
    [InlineData(DomainStock.StockAdjustmentSource.Nayax, 1)]
    public void Response_serializes_each_source_with_its_persisted_numeric_value(
        DomainStock.StockAdjustmentSource source, int expected)
    {
        var record = FullyPopulatedRecord() with { Source = source };

        var json = JsonSerializer.Serialize(StockAdjustmentResponseMapper.ToResponse(record), WebDefaults);

        using var document = JsonDocument.Parse(json);
        Assert.Equal(expected, document.RootElement.GetProperty("source").GetInt32());
    }

    /// <summary>
    /// The owning business is tenant state resolved from the authenticated actor, never part of the
    /// response (AGENTS.md § Tenant ownership). The entity hid it with <c>[JsonIgnore]</c>; the
    /// response does not carry it at all, even though the Application record still does.
    /// </summary>
    [Fact]
    public void Response_omits_the_owning_business_and_the_entity_navigations()
    {
        var json = JsonSerializer.Serialize(
            StockAdjustmentResponseMapper.ToResponse(FullyPopulatedRecord()), WebDefaults);

        Assert.DoesNotContain("businessId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"product\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("receiptItem\"", json, StringComparison.OrdinalIgnoreCase);
    }
}
