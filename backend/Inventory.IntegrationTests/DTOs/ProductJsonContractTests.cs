using System.Text.Json;
using Inventory.Application.Products;
using Inventory.Domain.Products;
using InventoryApi.Adapters.Mapping;
using InventoryApi.DTOs;
using Inventory.Infrastructure.Models;
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
///
/// <para><strong>The contract has been appended to twice, and broken once.</strong> Issue #736,
/// whose acceptance criteria require the catalogue response to say which fields the business's sales
/// provider manages so the UI can lock them, added <c>providerManagedFields</c>. Issue #737, whose
/// criterion is that Delete is offered only where the API allows it, added <c>canDelete</c> and
/// <c>deletionRefusal</c> after it - and only on a read that resolved the question, so they are
/// absent from the byte-for-byte comparisons below, which still run against a record that did not.
/// Every key a client already read keeps its name, type, value and position. The nested product
/// snapshots on a purchase, a supplier order and a machine slot carry none of the three - they are
/// <c>null</c> there and omitted.</para>
///
/// <para><strong>The one break is issue #787</strong> (A1 of #786), the destructive product-supplier
/// deletion the owner approved on 21 October 2026: <c>supplierId</c> and <c>supplier</c> are gone
/// from this response and from every nested product snapshot with it. They are <strong>removed, not
/// nulled</strong>, which is the honest shape - a product has no supplier to report, so reporting
/// one as empty would be a different statement. The purchase's and the supplier order's own
/// <c>supplier</c> is unaffected and their contract tests still assert it. Nothing else may move
/// this reference without its own issue.</para>
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

    /// <summary>
    /// The entity's JSON with issue #736's one appended key, which is the whole of the catalogue
    /// response's current contract. Written as an append to the reference value rather than as a
    /// second literal, so the test still fails if any legacy key moves by a single byte.
    /// </summary>
    private static string ExpectedJson(ProductRecord record, string providerManagedFields)
    {
        var entity = JsonSerializer.Serialize(EquivalentEntity(record), WebDefaults);
        return string.Concat(
            entity.AsSpan(0, entity.Length - 1),
            $",\"providerManagedFields\":{providerManagedFields}}}");
    }

    [Fact]
    public void Response_serializes_byte_for_byte_like_the_entity_it_replaced()
    {
        var record = FullyPopulatedRecord();

        var response = JsonSerializer.Serialize(
            ProductRecordResponseMapper.ToResponse(record, FinancialFigureVisibility.Visible), WebDefaults);

        Assert.Equal(ExpectedJson(record, "[]"), response);
    }

    /// <summary>
    /// The appended key for a product the business's sales provider names: the two fields
    /// <c>PUT /api/products/{id}</c> refuses a change to, in the order the one Domain statement of
    /// that rule lists them. A product nobody but this business maintains answers an empty array -
    /// a real answer, which the two byte-for-byte tests above assert.
    /// </summary>
    [Fact]
    public void Response_declares_the_provider_managed_fields_of_a_linked_product()
    {
        var record = FullyPopulatedRecord() with { IsProviderManaged = true };

        var response = JsonSerializer.Serialize(
            ProductRecordResponseMapper.ToResponse(record, FinancialFigureVisibility.Visible), WebDefaults);

        Assert.Equal(ExpectedJson(record, "[\"name\",\"categoryId\"]"), response);
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
            AverageUnitCost = 0m,
            QuantityInStock = 0,
            LowStockThreshold = 0,
            RestockTo = 0,
            IsActive = false,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        var response = JsonSerializer.Serialize(
            ProductRecordResponseMapper.ToResponse(record, FinancialFigureVisibility.Visible), WebDefaults);

        Assert.Equal(ExpectedJson(record, "[]"), response);
        Assert.Contains("\"category\":null", response, StringComparison.Ordinal);
        Assert.Contains("\"stockAdjustments\":[]", response, StringComparison.Ordinal);

        // The supplier keys are not null here - they are gone (issue #787). A client that still
        // reads `supplier` must find nothing rather than an explicit null that looks like
        // "no supplier recorded".
        Assert.DoesNotContain("supplier", response, StringComparison.OrdinalIgnoreCase);

        // The selling price went the same way (issue #792): `unitPrice` is absent, not `0`. A zero
        // would read as "this product is free" rather than "this product has no price".
        Assert.DoesNotContain("\"unitPrice\"", response, StringComparison.Ordinal);
    }

    [Fact]
    public void Response_exposes_the_same_keys_in_the_same_order_the_entity_did()
    {
        var json = JsonSerializer.Serialize(
            ProductRecordResponseMapper.ToResponse(FullyPopulatedRecord(), FinancialFigureVisibility.Visible),
            WebDefaults);

        using var document = JsonDocument.Parse(json);
        Assert.Equal(
            new[]
            {
                // "supplierId"/"supplier" (issue #787, A1 of #786) and "unitPrice" (issue #792, A2)
                // are the only keys this contract has ever lost, both by approved deletion of the
                // column behind them. "machinePrice" stays: the machine's live price is a different
                // value, and it is what a product now sells for.
                "id", "name", "sku", "description", "averageUnitCost", "costingQuantity",
                "inventoryValue", "machinePrice", "commissionValue", "suggestedNetValue",
                "suggestedPriceValue", "mdbCode", "maxStockInMachine", "machineReplenishmentNeed",
                "onOrderQuantity", "projectedStockForReorder", "quantityInStock", "lowStockThreshold",
                "restockTo", "needToOrder", "unit", "isActive", "lastEatBefore1", "lastEatBefore2",
                "createdAt", "updatedAt", "categoryId", "category",
                "stockAdjustments", "isLowStock", "isReorderAlert",

                // Issue #736's one appended key, last so every key above keeps its position. Issue
                // #737's canDelete/deletionRefusal follow it only on a read that resolved the
                // deletion question, which this record did not.
                "providerManagedFields",
            },
            document.RootElement.EnumerateObject().Select(property => property.Name));

        // The nested category is a snapshot, not an editing surface, so it says nothing about which
        // of its fields a provider manages and keeps exactly the three keys it always had.
        Assert.Equal(
            new[] { "id", "name", "description" },
            document.RootElement.GetProperty("category").EnumerateObject().Select(property => property.Name));

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
            ProductRecordResponseMapper.ToResponse(FullyPopulatedRecord(), FinancialFigureVisibility.Visible),
            WebDefaults);

        using var document = JsonDocument.Parse(json);
        Assert.Equal(3m, document.RootElement.GetProperty("projectedStockForReorder").GetDecimal());
        Assert.Equal(17m, document.RootElement.GetProperty("needToOrder").GetDecimal());
        Assert.True(document.RootElement.GetProperty("isLowStock").GetBoolean());
        Assert.True(document.RootElement.GetProperty("isReorderAlert").GetBoolean());
    }

    /// <summary>
    /// Issue #737's two appended keys, for a read that resolved the deletion question: whether the
    /// delete would be allowed and, when it would not, the very message the delete answers
    /// <c>409</c> with. They come after issue #736's key, so again every key a client already read
    /// keeps its name, type, value and position.
    /// </summary>
    [Fact]
    public void Response_publishes_the_refusal_a_delete_would_give()
    {
        var references = new ProductDeletionReferences(1, 0, 0, 0, 0, 0);
        var record = FullyPopulatedRecord() with
        {
            DeletionEligibility = ProductDeletionEligibility.Decide(
                isProviderManaged: false, provider: null, references),
        };

        var json = JsonSerializer.Serialize(
            ProductRecordResponseMapper.ToResponse(record, FinancialFigureVisibility.Visible), WebDefaults);

        using var document = JsonDocument.Parse(json);
        Assert.False(document.RootElement.GetProperty("canDelete").GetBoolean());
        Assert.Equal(
            ProductDeletionPolicy.Refusal(isProviderManaged: false, provider: null, references),
            document.RootElement.GetProperty("deletionRefusal").GetString());
        Assert.Equal(
            ["providerManagedFields", "canDelete", "deletionRefusal"],
            document.RootElement.EnumerateObject().Select(property => property.Name).TakeLast(3));
    }

    /// <summary>
    /// A deletable product says so and carries no reason: an empty refusal would read as a refusal
    /// with nothing to show.
    /// </summary>
    [Fact]
    public void Response_says_a_product_nothing_refers_to_may_be_deleted()
    {
        var record = FullyPopulatedRecord() with
        {
            DeletionEligibility = ProductDeletionEligibility.Decide(
                isProviderManaged: false, provider: null, ProductDeletionReferences.None),
        };

        var json = JsonSerializer.Serialize(
            ProductRecordResponseMapper.ToResponse(record, FinancialFigureVisibility.Visible), WebDefaults);

        using var document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.GetProperty("canDelete").GetBoolean());
        Assert.False(document.RootElement.TryGetProperty("deletionRefusal", out _));
    }

    /// <summary>
    /// A read that did not resolve the question says nothing at all - both keys are absent, which is
    /// what the byte-for-byte comparisons above are still asserting. An absent <c>canDelete</c> is
    /// not permission, and a client that treats it as one offers a delete the server may refuse.
    /// </summary>
    [Fact]
    public void Response_omits_the_deletion_answer_when_the_read_did_not_resolve_it()
    {
        var json = JsonSerializer.Serialize(
            ProductRecordResponseMapper.ToResponse(FullyPopulatedRecord(), FinancialFigureVisibility.Visible),
            WebDefaults);

        using var document = JsonDocument.Parse(json);
        Assert.False(document.RootElement.TryGetProperty("canDelete", out _));
        Assert.False(document.RootElement.TryGetProperty("deletionRefusal", out _));
    }

    [Fact]
    public void Response_omits_the_owning_business()
    {
        var json = JsonSerializer.Serialize(
            ProductRecordResponseMapper.ToResponse(FullyPopulatedRecord(), FinancialFigureVisibility.Visible),
            WebDefaults);

        Assert.DoesNotContain("businessId", json, StringComparison.OrdinalIgnoreCase);
    }
}
