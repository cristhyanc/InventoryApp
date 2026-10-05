using System.Text.Json;
using Inventory.Application.Machines;
using Inventory.Application.Products;
using InventoryApi.Adapters.Mapping;
using InventoryApi.DTOs;
using InventoryApi.Models;
using Xunit;

namespace InventoryApi.Tests.DTOs;

/// <summary>
/// Locks the wire contract of the "/api/machines" endpoints now that
/// <see cref="MachineResponse"/> and <see cref="ProductResponse"/> replaced the EF
/// <c>Machine</c>/<c>Product</c> entities <c>MachinesController</c> used to serialise through the
/// retired <c>MachineService</c> delegator (issue #302).
///
/// The reference value is the entity itself: each test builds the <c>Machine</c>/<c>Product</c> the
/// delegator would have returned for the same Application record and compares the serialised
/// bytes, so a key that is renamed, reordered, added, dropped, or given a different value fails
/// here rather than in a client. The explicit key lists are asserted as well, so a future change to
/// <em>both</em> sides at once still has to be a conscious one.
/// </summary>
public class MachineJsonContractTests
{
    private static readonly JsonSerializerOptions WebDefaults = new(JsonSerializerDefaults.Web);

    private static MachineSummary FullyPopulatedSummary() => new(
        MachineId: 42,
        MachineName: "Pavillion Left",
        MachineNumber: "PL-1",
        ActorId: 91,
        TodayGrossRevenue: 12.30m,
        CurrentWeekGrossRevenue: 45.60m,
        PreviousComparableWeekGrossRevenue: 33.33m,
        LastWeekGrossRevenue: 78.90m,
        MonthToDateGrossRevenue: 123.45m,
        TwoWeeksAgoGrossRevenue: 67.89m,
        TodayDirectProfit: 3.21m,
        CurrentWeekDirectProfit: 9.87m,
        PreviousComparableWeekDirectProfit: 6.54m,
        LastWeekDirectProfit: 11.11m,
        MonthToDateDirectProfit: 22.22m,
        TwoWeeksAgoDirectProfit: 8.08m,
        ProfitabilityStatus: "Direct profit unavailable: 2 sales without persisted COGS.");

    /// <summary>
    /// The <c>Machine</c> the retired <c>MachineService</c> returned for <paramref name="summary"/>,
    /// rebuilt here as the reference wire shape.
    /// </summary>
    private static Machine EquivalentEntity(MachineSummary summary) => new()
    {
        ActorID = summary.ActorId,
        MachineID = summary.MachineId,
        MachineName = summary.MachineName,
        MachineNumber = summary.MachineNumber,
        TodayGrossRevenue = summary.TodayGrossRevenue,
        CurrentWeekGrossRevenue = summary.CurrentWeekGrossRevenue,
        PreviousComparableWeekGrossRevenue = summary.PreviousComparableWeekGrossRevenue,
        LastWeekGrossRevenue = summary.LastWeekGrossRevenue,
        MonthToDateGrossRevenue = summary.MonthToDateGrossRevenue,
        TwoWeeksAgoGrossRevenue = summary.TwoWeeksAgoGrossRevenue,
        TodayDirectProfit = summary.TodayDirectProfit,
        CurrentWeekDirectProfit = summary.CurrentWeekDirectProfit,
        PreviousComparableWeekDirectProfit = summary.PreviousComparableWeekDirectProfit,
        LastWeekDirectProfit = summary.LastWeekDirectProfit,
        MonthToDateDirectProfit = summary.MonthToDateDirectProfit,
        TwoWeeksAgoDirectProfit = summary.TwoWeeksAgoDirectProfit,
        ProfitabilityStatus = summary.ProfitabilityStatus,
    };

    [Fact]
    public void Machine_response_serialises_byte_for_byte_like_the_entity_it_replaced()
    {
        var summary = FullyPopulatedSummary();

        var response = JsonSerializer.Serialize(MachineResponseMapper.ToResponse(summary), WebDefaults);

        Assert.Equal(JsonSerializer.Serialize(EquivalentEntity(summary), WebDefaults), response);
    }

    /// <summary>
    /// The unprofitable-or-unknown read path: a machine Nayax returns with no name/number, no
    /// resolved site actor and no calculable direct profit. Every unavailable profit must stay an
    /// explicit null rather than becoming zero or being omitted (AGENTS.md § Profit calculations).
    /// </summary>
    [Fact]
    public void Machine_response_serialises_byte_for_byte_like_the_entity_when_profit_is_unavailable()
    {
        var summary = new MachineSummary(
            MachineId: 7,
            MachineName: null,
            MachineNumber: null,
            ActorId: null,
            TodayGrossRevenue: 0m,
            CurrentWeekGrossRevenue: 0m,
            PreviousComparableWeekGrossRevenue: 0m,
            LastWeekGrossRevenue: 0m,
            MonthToDateGrossRevenue: 0m,
            TwoWeeksAgoGrossRevenue: 0m,
            TodayDirectProfit: null,
            CurrentWeekDirectProfit: null,
            PreviousComparableWeekDirectProfit: null,
            LastWeekDirectProfit: null,
            MonthToDateDirectProfit: null,
            TwoWeeksAgoDirectProfit: null,
            ProfitabilityStatus: null);

        var response = JsonSerializer.Serialize(MachineResponseMapper.ToResponse(summary), WebDefaults);

        Assert.Equal(JsonSerializer.Serialize(EquivalentEntity(summary), WebDefaults), response);
        Assert.Contains("\"machineName\":null", response, StringComparison.Ordinal);
        Assert.Contains("\"actorID\":null", response, StringComparison.Ordinal);
        Assert.Contains("\"todayDirectProfit\":null", response, StringComparison.Ordinal);
        Assert.Contains("\"profitabilityStatus\":null", response, StringComparison.Ordinal);
    }

    /// <summary>
    /// The keys clients read, including the two the entity spelled with a trailing <c>ID</c> rather
    /// than <c>Id</c>: the camel-case naming policy turns those into <c>machineID</c>/<c>actorID</c>,
    /// and the dashboard and machine-detail components bind to exactly those names.
    /// </summary>
    [Fact]
    public void Machine_response_exposes_the_same_keys_in_the_same_order_the_entity_did()
    {
        var json = JsonSerializer.Serialize(
            MachineResponseMapper.ToResponse(FullyPopulatedSummary()), WebDefaults);

        using var document = JsonDocument.Parse(json);
        Assert.Equal(
            new[]
            {
                "machineID", "machineName", "machineNumber", "actorID", "todayGrossRevenue",
                "currentWeekGrossRevenue", "lastWeekGrossRevenue", "twoWeeksAgoGrossRevenue",
                "todayDirectProfit", "twoWeeksAgoDirectProfit", "lastWeekDirectProfit",
                "currentWeekDirectProfit", "previousComparableWeekGrossRevenue",
                "previousComparableWeekDirectProfit", "monthToDateGrossRevenue",
                "monthToDateDirectProfit", "profitabilityStatus",
            },
            document.RootElement.EnumerateObject().Select(property => property.Name));
    }

    private static MachineProductRecord FullyPopulatedSlot() => new()
    {
        Product = new ProductRecord
        {
            Id = 200,
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
                    200,
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
                    42,
                    "Initial",
                    new DateTime(2026, 6, 1),
                    new DateTime(2026, 1, 1, 8, 30, 0, DateTimeKind.Utc),
                    new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc)),
            ],
        },
        MachinePrice = 5m,
        CommissionValue = 10m,
        MdbCode = 13,
        QuantityInStock = 2,
        MaxStockInMachine = 6,
        SuggestedNetValue = 2.78m,
        SuggestedPriceValue = 4.44m,
    };

    [Fact]
    public void Machine_product_response_serialises_byte_for_byte_like_the_entity_it_replaced()
    {
        var slot = FullyPopulatedSlot();

        var response = JsonSerializer.Serialize(ProductRecordResponseMapper.ToResponse(slot), WebDefaults);

        Assert.Equal(
            JsonSerializer.Serialize(LegacyProductEntityShape.ForMachineSlot(slot), WebDefaults),
            response);
    }

    /// <summary>
    /// The sparse slot: a Nayax mapping with no price, no commission, no MDB code and no resolvable
    /// suggested pricing, over a bare catalogue product. An unavailable suggestion stays null, not
    /// zero, and the slot's own stock still wins over the product's storage stock.
    /// </summary>
    [Fact]
    public void Machine_product_response_serialises_byte_for_byte_like_the_entity_for_a_bare_slot()
    {
        var slot = new MachineProductRecord
        {
            Product = new ProductRecord
            {
                Id = 201,
                Name = "Bare",
                UnitPrice = 0m,
                AverageUnitCost = 0m,
                QuantityInStock = 9,
                LowStockThreshold = 0,
                RestockTo = 0,
                IsActive = false,
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            },
            MachinePrice = 0m,
            CommissionValue = 0m,
            QuantityInStock = 0,
        };

        var response = JsonSerializer.Serialize(ProductRecordResponseMapper.ToResponse(slot), WebDefaults);

        Assert.Equal(
            JsonSerializer.Serialize(LegacyProductEntityShape.ForMachineSlot(slot), WebDefaults),
            response);
        Assert.Contains("\"mdbCode\":null", response, StringComparison.Ordinal);
        Assert.Contains("\"maxStockInMachine\":null", response, StringComparison.Ordinal);
        Assert.Contains("\"suggestedNetValue\":null", response, StringComparison.Ordinal);
        Assert.Contains("\"quantityInStock\":0", response, StringComparison.Ordinal);
    }

    /// <summary>
    /// The machine-slot keys, pinned separately from the byte comparison because this response and
    /// the catalogue one share a single DTO: the machine view must still carry the complete product
    /// shape in the catalogue's order, with the slot values overlaid in place.
    /// </summary>
    [Fact]
    public void Machine_product_response_carries_the_full_product_shape_with_the_slot_values_overlaid()
    {
        var json = JsonSerializer.Serialize(
            ProductRecordResponseMapper.ToResponse(FullyPopulatedSlot()), WebDefaults);

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

        Assert.Equal(5m, document.RootElement.GetProperty("machinePrice").GetDecimal());
        Assert.Equal(10m, document.RootElement.GetProperty("commissionValue").GetDecimal());
        Assert.Equal(13, document.RootElement.GetProperty("mdbCode").GetInt32());
        Assert.Equal(6, document.RootElement.GetProperty("maxStockInMachine").GetInt32());
        Assert.Equal(2.78m, document.RootElement.GetProperty("suggestedNetValue").GetDecimal());
        Assert.Equal(4.44m, document.RootElement.GetProperty("suggestedPriceValue").GetDecimal());

        // The slot's own stock, not the product's storage stock of 4.
        Assert.Equal(2, document.RootElement.GetProperty("quantityInStock").GetInt32());
    }

    /// <summary>
    /// The derived reorder values a machine-slot row carries. They must keep coming from the one
    /// authoritative <c>ProductReorderPolicy</c> over the slot's own stock, exactly as the entity
    /// computed them from the same overlaid values: projected stock 2 in the slot + 0 on order - 0
    /// machine need = 2, at or below the threshold of 10, so the reorder quantity is the restock
    /// target 20 net of the 2 on hand.
    /// </summary>
    [Fact]
    public void Machine_product_response_derives_the_reorder_values_from_the_slot_stock()
    {
        var slot = FullyPopulatedSlot();

        var json = JsonSerializer.Serialize(ProductRecordResponseMapper.ToResponse(slot), WebDefaults);

        using var document = JsonDocument.Parse(json);
        Assert.Equal(2m, document.RootElement.GetProperty("projectedStockForReorder").GetDecimal());
        Assert.Equal(18m, document.RootElement.GetProperty("needToOrder").GetDecimal());
        Assert.True(document.RootElement.GetProperty("isLowStock").GetBoolean());
        Assert.True(document.RootElement.GetProperty("isReorderAlert").GetBoolean());

        var entity = LegacyProductEntityShape.ForMachineSlot(slot);
        Assert.Equal(entity.ProjectedStockForReorder, document.RootElement.GetProperty("projectedStockForReorder").GetDecimal());
        Assert.Equal(entity.NeedToOrder, document.RootElement.GetProperty("needToOrder").GetDecimal());
    }

    [Fact]
    public void Machine_responses_omit_the_owning_business()
    {
        var machine = JsonSerializer.Serialize(
            MachineResponseMapper.ToResponse(FullyPopulatedSummary()), WebDefaults);
        var product = JsonSerializer.Serialize(
            ProductRecordResponseMapper.ToResponse(FullyPopulatedSlot()), WebDefaults);

        Assert.DoesNotContain("businessId", machine, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("businessId", product, StringComparison.OrdinalIgnoreCase);
    }
}
