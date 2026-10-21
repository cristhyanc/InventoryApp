using System.Text.Json;
using Inventory.Application.SupplierOrders;
using InventoryApi.Adapters.Mapping;
using InventoryApi.DTOs;
using Inventory.Infrastructure.Models;
using Xunit;

namespace InventoryApi.Tests.DTOs;

/// <summary>
/// Locks the wire contract of the "/api/supplierorders" endpoints now that
/// <see cref="SupplierOrderResponse"/> replaced the EF <c>SupplierOrder</c> entity the retired
/// <c>SupplierOrderService</c> rebuilt for them (issue #304).
///
/// The reference value is the entity itself: each test builds the <c>SupplierOrder</c> the retired
/// delegator would have returned for the same Application record and compares the serialized
/// bytes, so a key that is renamed, reordered, added, dropped, or given a different value fails
/// here rather than in a client. The explicit key list is asserted as well, so a future change to
/// <em>both</em> sides at once still has to be a conscious one.
///
/// Every byte-for-byte comparison here is of the <see cref="FinancialFigureVisibility.Visible"/>
/// projection, stated explicitly since issue #700, which is what asserts that a Manager's and an
/// Owner's supplier-order response is unchanged by that slice. The redacted projection withholds
/// exactly one key's value and is pinned by
/// <see cref="Response_withholds_only_the_nested_products_inventory_value_when_redacted"/>.
/// </summary>
public class SupplierOrderJsonContractTests
{
    private const FinancialFigureVisibility Visible = FinancialFigureVisibility.Visible;

    private static readonly JsonSerializerOptions WebDefaults = new(JsonSerializerDefaults.Web);

    private static SupplierOrderProductSummaryRecord ProductRecord() => new(
        3,
        "Coke",
        "SKU-1",
        "A can",
        1.25m,
        7,
        8.75m,
        4,
        10,
        20,
        "can",
        true,
        new DateTime(2026, 1, 1, 8, 30, 0, DateTimeKind.Utc),
        new DateTime(2026, 2, 2, 9, 45, 0, DateTimeKind.Utc),
        5);

    private static SupplierOrderRecord RecordWith(
        Inventory.Domain.SupplierOrders.SupplierOrderStatus status,
        decimal quantityOrdered = 24m,
        decimal quantityReceived = 6m) => new(
        9,
        BusinessId: 2,
        4,
        new SupplierOrderSupplierRecord(4, "Acme", "Pat", "123", "a@b.c", "1 Road"),
        new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc),
        new DateTime(2026, 9, 10, 8, 0, 0, DateTimeKind.Utc),
        "Order #42",
        "Priority",
        status,
        new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc),
        new DateTime(2026, 9, 2, 8, 0, 0, DateTimeKind.Utc),
        [new SupplierOrderLineRecord(11, 9, 3, ProductRecord(), quantityOrdered, quantityReceived, 0.50m, "Case of 24")]);

    /// <summary>
    /// The <c>SupplierOrder</c> the retired <c>SupplierOrderService</c> returned for
    /// <paramref name="record"/>, rebuilt here as the reference wire shape. It is the former
    /// <c>SupplierOrderResponseMapper.ToSupplierOrder</c> projection, including wiring each
    /// reconstructed line back to its reconstructed parent order - without that, the entity's
    /// computed <c>OutstandingQuantity</c> could not see the order's cancelled state and the
    /// reference value itself would be wrong.
    /// </summary>
    private static SupplierOrder EquivalentEntity(SupplierOrderRecord record)
    {
        var order = new SupplierOrder
        {
            Id = record.Id,
            BusinessId = record.BusinessId,
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
            OrderDate = record.OrderDate,
            ExpectedDate = record.ExpectedDate,
            Reference = record.Reference,
            Notes = record.Notes,
            Status = (SupplierOrderStatus)record.Status,
            CreatedAt = record.CreatedAt,
            UpdatedAt = record.UpdatedAt,
        };

        order.Lines = record.Lines.Select(line => new SupplierOrderLine
        {
            Id = line.Id,
            SupplierOrderId = line.SupplierOrderId,
            SupplierOrder = order,
            ProductId = line.ProductId,
            Product = new Product
            {
                Id = line.Product.Id,
                Name = line.Product.Name,
                Sku = line.Product.Sku,
                Description = line.Product.Description,
                AverageUnitCost = line.Product.AverageUnitCost,
                CostingQuantity = line.Product.CostingQuantity,
                InventoryValue = line.Product.InventoryValue,
                QuantityInStock = line.Product.QuantityInStock,
                LowStockThreshold = line.Product.LowStockThreshold,
                RestockTo = line.Product.RestockTo,
                Unit = line.Product.Unit,
                IsActive = line.Product.IsActive,
                CreatedAt = line.Product.CreatedAt,
                UpdatedAt = line.Product.UpdatedAt,
                CategoryId = line.Product.CategoryId,
            },
            QuantityOrdered = line.QuantityOrdered,
            QuantityReceived = line.QuantityReceived,
            UnitPrice = line.UnitPrice,
            Notes = line.Notes,
        }).ToList();

        return order;
    }

    [Theory]
    [InlineData(Inventory.Domain.SupplierOrders.SupplierOrderStatus.Ordered)]
    [InlineData(Inventory.Domain.SupplierOrders.SupplierOrderStatus.PartiallyReceived)]
    [InlineData(Inventory.Domain.SupplierOrders.SupplierOrderStatus.Received)]
    [InlineData(Inventory.Domain.SupplierOrders.SupplierOrderStatus.Cancelled)]
    public void Response_serializes_byte_for_byte_like_the_entity_it_replaced(
        Inventory.Domain.SupplierOrders.SupplierOrderStatus status)
    {
        var record = RecordWith(status);

        var response = JsonSerializer.Serialize(SupplierOrderResponseMapper.ToResponse(record, Visible), WebDefaults);

        Assert.Equal(JsonSerializer.Serialize(EquivalentEntity(record), WebDefaults), response);
    }

    [Fact]
    public void Response_serializes_byte_for_byte_like_the_entity_when_no_supplier_is_loaded()
    {
        var record = RecordWith(Inventory.Domain.SupplierOrders.SupplierOrderStatus.Ordered) with
        {
            SupplierId = null,
            Supplier = null,
            ExpectedDate = null,
            Reference = null,
            Notes = null,
        };

        var response = JsonSerializer.Serialize(SupplierOrderResponseMapper.ToResponse(record, Visible), WebDefaults);

        Assert.Equal(JsonSerializer.Serialize(EquivalentEntity(record), WebDefaults), response);
        Assert.Contains("\"supplier\":null", response, StringComparison.Ordinal);
    }

    [Fact]
    public void Response_exposes_the_same_keys_in_the_same_order_the_entity_did()
    {
        var json = JsonSerializer.Serialize(
            SupplierOrderResponseMapper.ToResponse(
                RecordWith(Inventory.Domain.SupplierOrders.SupplierOrderStatus.PartiallyReceived), Visible),
            WebDefaults);

        using var document = JsonDocument.Parse(json);
        Assert.Equal(
            new[]
            {
                "id", "supplierId", "supplier", "orderDate", "expectedDate", "reference", "notes",
                "status", "createdAt", "updatedAt", "lines",
            },
            document.RootElement.EnumerateObject().Select(property => property.Name));

        Assert.Equal(
            new[] { "id", "name", "contactName", "phone", "email", "address" },
            document.RootElement.GetProperty("supplier").EnumerateObject().Select(property => property.Name));

        Assert.Equal(
            new[]
            {
                "id", "supplierOrderId", "productId", "product", "quantityOrdered", "quantityReceived",
                "unitPrice", "notes", "outstandingQuantity",
            },
            document.RootElement.GetProperty("lines")[0].EnumerateObject().Select(property => property.Name));
    }

    /// <summary>
    /// The nested product snapshot carries no supplier of its own (issue #787, A1 of #786), while
    /// the order's own supplier - the one a person chose when placing it - is reported in full with
    /// its complete nested shape. Those are two different suppliers, and only the product's is gone.
    /// </summary>
    [Fact]
    public void Response_drops_the_supplier_from_the_nested_product_and_keeps_the_orders_own()
    {
        var json = JsonSerializer.Serialize(
            SupplierOrderResponseMapper.ToResponse(
                RecordWith(Inventory.Domain.SupplierOrders.SupplierOrderStatus.PartiallyReceived), Visible),
            WebDefaults);

        using var document = JsonDocument.Parse(json);
        var line = document.RootElement.GetProperty("lines")[0];
        var product = line.GetProperty("product");
        Assert.False(product.TryGetProperty("supplierId", out _));
        Assert.False(product.TryGetProperty("supplier", out _));
        // And no selling price since issue #792 (A2 of #786) - while the line's own agreed
        // `unitPrice`, which is a different value entirely, is untouched.
        Assert.False(product.TryGetProperty("unitPrice", out _));
        // Issue #788's availability projection is not here either: it belongs to the catalogue
        // reads, and this snapshot is a pinned contract of its own. The stored `averageUnitCost`
        // below is unchanged, so nothing a client already read from this payload has moved.
        Assert.False(product.TryGetProperty("averageUnitCostForDisplay", out _));
        Assert.Equal(0.50m, line.GetProperty("unitPrice").GetDecimal());
        Assert.Equal("Coke", product.GetProperty("name").GetString());
        Assert.Equal(1.25m, product.GetProperty("averageUnitCost").GetDecimal());
        Assert.Equal((long?)5, product.GetProperty("categoryId").GetInt64());

        Assert.Equal(
            new[] { "id", "name", "contactName", "phone", "email", "address" },
            document.RootElement.GetProperty("supplier").EnumerateObject().Select(property => property.Name));
    }

    /// <summary>
    /// The status stays the persisted numeric value clients already switch on, and must keep
    /// matching <c>Inventory.Infrastructure.Models.SupplierOrderStatus</c> member for member even though the
    /// response now carries the Domain enum.
    /// </summary>
    [Theory]
    [InlineData(Inventory.Domain.SupplierOrders.SupplierOrderStatus.Ordered, 0)]
    [InlineData(Inventory.Domain.SupplierOrders.SupplierOrderStatus.PartiallyReceived, 1)]
    [InlineData(Inventory.Domain.SupplierOrders.SupplierOrderStatus.Received, 2)]
    [InlineData(Inventory.Domain.SupplierOrders.SupplierOrderStatus.Cancelled, 3)]
    public void Response_serializes_the_status_as_its_persisted_numeric_value(
        Inventory.Domain.SupplierOrders.SupplierOrderStatus status, int expected)
    {
        var json = JsonSerializer.Serialize(
            SupplierOrderResponseMapper.ToResponse(RecordWith(status), Visible), WebDefaults);

        using var document = JsonDocument.Parse(json);
        Assert.Equal(expected, document.RootElement.GetProperty("status").GetInt32());
    }

    /// <summary>
    /// The outstanding quantity clients read off each line. It is the one rule the entity computed
    /// from its parent order's state: zero for a cancelled order, the unreceived remainder
    /// otherwise, never negative when more arrived than was ordered.
    /// </summary>
    [Theory]
    [InlineData(Inventory.Domain.SupplierOrders.SupplierOrderStatus.Ordered, 24, 0, 24)]
    [InlineData(Inventory.Domain.SupplierOrders.SupplierOrderStatus.PartiallyReceived, 24, 6, 18)]
    [InlineData(Inventory.Domain.SupplierOrders.SupplierOrderStatus.Received, 24, 24, 0)]
    [InlineData(Inventory.Domain.SupplierOrders.SupplierOrderStatus.Received, 24, 30, 0)]
    [InlineData(Inventory.Domain.SupplierOrders.SupplierOrderStatus.Cancelled, 24, 0, 0)]
    public void Response_derives_the_outstanding_quantity_the_entity_computed(
        Inventory.Domain.SupplierOrders.SupplierOrderStatus status,
        decimal quantityOrdered,
        decimal quantityReceived,
        decimal expected)
    {
        var record = RecordWith(status, quantityOrdered, quantityReceived);

        var json = JsonSerializer.Serialize(SupplierOrderResponseMapper.ToResponse(record, Visible), WebDefaults);

        using var document = JsonDocument.Parse(json);
        Assert.Equal(expected, document.RootElement.GetProperty("lines")[0].GetProperty("outstandingQuantity").GetDecimal());
        Assert.Equal(JsonSerializer.Serialize(EquivalentEntity(record), WebDefaults), json);
    }

    [Fact]
    public void Response_omits_the_owning_business_and_the_parent_order_back_reference()
    {
        var json = JsonSerializer.Serialize(
            SupplierOrderResponseMapper.ToResponse(
                RecordWith(Inventory.Domain.SupplierOrders.SupplierOrderStatus.Ordered), Visible),
            WebDefaults);

        Assert.DoesNotContain("businessId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("receiptAllocations", json, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The redacted projection (issue #700) withholds the nested catalogue product's
    /// <c>inventoryValue</c> and changes nothing else: every key stays, in its position, and what
    /// was ordered, at what unit price, and how much is still outstanding are value-for-value the
    /// visible projection's.
    /// </summary>
    [Fact]
    public void Response_withholds_only_the_nested_products_inventory_value_when_redacted()
    {
        var record = RecordWith(Inventory.Domain.SupplierOrders.SupplierOrderStatus.PartiallyReceived);

        var redacted = JsonSerializer.Serialize(
            SupplierOrderResponseMapper.ToResponse(record, FinancialFigureVisibility.Redacted), WebDefaults);

        var visible = JsonSerializer.Serialize(SupplierOrderResponseMapper.ToResponse(record, Visible), WebDefaults);
        Assert.Contains("\"inventoryValue\":8.75", visible, StringComparison.Ordinal);
        Assert.Equal(
            visible.Replace("\"inventoryValue\":8.75", "\"inventoryValue\":null", StringComparison.Ordinal),
            redacted);

        using var document = JsonDocument.Parse(redacted);
        var line = document.RootElement.GetProperty("lines")[0];
        Assert.Equal(JsonValueKind.Null, line.GetProperty("product").GetProperty("inventoryValue").ValueKind);

        // Not zeroed, and the purchasing figures beside it are untouched.
        Assert.Equal(1.25m, line.GetProperty("product").GetProperty("averageUnitCost").GetDecimal());
        Assert.Equal(0.50m, line.GetProperty("unitPrice").GetDecimal());
        Assert.Equal(18m, line.GetProperty("outstandingQuantity").GetDecimal());
    }
}
