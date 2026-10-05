using System.Text.Json;
using Inventory.Application.Purchases;
using InventoryApi.Adapters.Mapping;
using InventoryApi.DTOs;
using Inventory.Infrastructure.Models;
using Xunit;

namespace InventoryApi.Tests.DTOs;

/// <summary>
/// Locks the wire contract of the "/api/purchases" endpoints now that
/// <see cref="PurchaseResponse"/> replaced the EF <c>Purchase</c> entity the retired
/// <c>PurchaseService</c> rebuilt for them (issue #304).
///
/// The reference value is the entity itself: each test builds the <c>Purchase</c> the retired
/// delegator would have returned for the same Application record and compares the serialized
/// bytes, so a key that is renamed, reordered, added, dropped, or given a different value fails
/// here rather than in a client. The explicit key list is asserted as well, so a future change to
/// <em>both</em> sides at once still has to be a conscious one.
///
/// <see cref="PurchaseJsonContractTests"/> keeps covering the "purchase"/"validation" envelope
/// itself (issue #127); this file covers what is now inside the "purchase" key.
/// </summary>
public class PurchaseResponseJsonContractTests
{
    private static readonly JsonSerializerOptions WebDefaults = new(JsonSerializerDefaults.Web);

    private static PurchaseProductSummaryRecord ProductRecord() => new(
        3,
        "Coke",
        "SKU-1",
        "A can",
        3.50m,
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
        5,
        4);

    private static PurchaseRecord FullyPopulatedRecord() => new(
        7,
        BusinessId: 2,
        "Weekly restock",
        "Supplier note",
        65.40m,
        5m,
        2m,
        new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc),
        4,
        new PurchaseSupplierRecord(4, "Acme", "Pat", "123", "a@b.c", "1 Road"),
        [new PurchaseItemRecord(11, 7, 3, 24m, 1.15m, ProductRecord())],
        "scan.jpg",
        "abc-def.jpg",
        "image/jpeg",
        2048,
        new DateTime(2026, 3, 1, 10, 5, 0, DateTimeKind.Utc));

    /// <summary>
    /// The <c>Purchase</c> the retired <c>PurchaseService</c> returned for <paramref name="record"/>,
    /// rebuilt here as the reference wire shape. It is the former
    /// <c>PurchaseResponseMapper.ToPurchase</c> projection, kept in the test so the production code
    /// no longer has to construct an entity to describe its own contract.
    /// </summary>
    private static Purchase EquivalentEntity(PurchaseRecord record) => new()
    {
        BusinessId = record.BusinessId,
        Id = record.Id,
        Title = record.Title,
        Notes = record.Notes,
        TotalAmount = record.TotalAmount,
        DeliveryCost = record.DeliveryCost,
        PackageCost = record.PackageCost,
        PurchaseDate = record.PurchaseDate,
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
        Items = record.Items.Select(item => new PurchaseItem
        {
            Id = item.Id,
            ReceiptId = item.ReceiptId,
            ProductId = item.ProductId,
            Quantity = item.Quantity,
            UnitCost = item.UnitCost,
            Product = item.Product is null
                ? null
                : new Product
                {
                    Id = item.Product.Id,
                    Name = item.Product.Name,
                    Sku = item.Product.Sku,
                    Description = item.Product.Description,
                    UnitPrice = item.Product.UnitPrice,
                    AverageUnitCost = item.Product.AverageUnitCost,
                    CostingQuantity = item.Product.CostingQuantity,
                    InventoryValue = item.Product.InventoryValue,
                    QuantityInStock = item.Product.QuantityInStock,
                    LowStockThreshold = item.Product.LowStockThreshold,
                    RestockTo = item.Product.RestockTo,
                    Unit = item.Product.Unit,
                    IsActive = item.Product.IsActive,
                    CreatedAt = item.Product.CreatedAt,
                    UpdatedAt = item.Product.UpdatedAt,
                    CategoryId = item.Product.CategoryId,
                    SupplierId = item.Product.SupplierId,
                },
        }).ToList(),
        FileName = record.FileName,
        StoredFileName = record.StoredFileName,
        ContentType = record.ContentType,
        FileSizeBytes = record.FileSizeBytes,
        CreatedAt = record.CreatedAt,
    };

    [Fact]
    public void Response_serializes_byte_for_byte_like_the_entity_it_replaced()
    {
        var record = FullyPopulatedRecord();

        var response = JsonSerializer.Serialize(PurchaseResponseMapper.ToResponse(record), WebDefaults);

        Assert.Equal(JsonSerializer.Serialize(EquivalentEntity(record), WebDefaults), response);
    }

    /// <summary>
    /// A newly uploaded purchase: no supplier detail, and line items whose product navigation the
    /// retired service never loaded. The absent navigations must still be emitted as explicit
    /// nulls, not omitted.
    /// </summary>
    [Fact]
    public void Response_serializes_byte_for_byte_like_the_entity_when_no_navigation_is_loaded()
    {
        var record = FullyPopulatedRecord() with
        {
            SupplierId = null,
            Supplier = null,
            Items = [new PurchaseItemRecord(11, 7, 3, 24m, 1.15m, Product: null)],
        };

        var response = JsonSerializer.Serialize(PurchaseResponseMapper.ToResponse(record), WebDefaults);

        Assert.Equal(JsonSerializer.Serialize(EquivalentEntity(record), WebDefaults), response);
        Assert.Contains("\"supplier\":null", response, StringComparison.Ordinal);
        Assert.Contains("\"product\":null", response, StringComparison.Ordinal);
    }

    [Fact]
    public void Response_serializes_byte_for_byte_like_the_entity_when_it_has_no_items()
    {
        var record = FullyPopulatedRecord() with { Items = [] };

        var response = JsonSerializer.Serialize(PurchaseResponseMapper.ToResponse(record), WebDefaults);

        Assert.Equal(JsonSerializer.Serialize(EquivalentEntity(record), WebDefaults), response);
        Assert.Contains("\"items\":[]", response, StringComparison.Ordinal);
    }

    [Fact]
    public void Response_exposes_the_same_keys_in_the_same_order_the_entity_did()
    {
        var json = JsonSerializer.Serialize(
            PurchaseResponseMapper.ToResponse(FullyPopulatedRecord()), WebDefaults);

        using var document = JsonDocument.Parse(json);
        Assert.Equal(
            new[]
            {
                "id", "title", "notes", "totalAmount", "deliveryCost", "packageCost", "purchaseDate",
                "supplierId", "supplier", "items", "fileName", "storedFileName", "contentType",
                "fileSizeBytes", "createdAt",
            },
            document.RootElement.EnumerateObject().Select(property => property.Name));

        Assert.Equal(
            new[] { "id", "name", "contactName", "phone", "email", "address" },
            document.RootElement.GetProperty("supplier").EnumerateObject().Select(property => property.Name));

        Assert.Equal(
            new[] { "id", "receiptId", "productId", "product", "quantity", "unitCost", "lineTotal" },
            document.RootElement.GetProperty("items")[0].EnumerateObject().Select(property => property.Name));
    }

    /// <summary>
    /// The line total clients read straight off each item, unchanged from the entity's
    /// <c>[NotMapped]</c> computed value: 24 units at $1.15.
    /// </summary>
    [Fact]
    public void Response_derives_each_item_line_total_from_its_quantity_and_unit_cost()
    {
        var json = JsonSerializer.Serialize(
            PurchaseResponseMapper.ToResponse(FullyPopulatedRecord()), WebDefaults);

        using var document = JsonDocument.Parse(json);
        Assert.Equal(27.60m, document.RootElement.GetProperty("items")[0].GetProperty("lineTotal").GetDecimal());
    }

    /// <summary>
    /// The owning business is tenant state resolved from the authenticated actor, never part of the
    /// response (AGENTS.md § Tenant ownership). The entity hid it with <c>[JsonIgnore]</c>; the
    /// response does not carry it at all.
    /// </summary>
    [Fact]
    public void Response_omits_the_owning_business()
    {
        var json = JsonSerializer.Serialize(
            PurchaseResponseMapper.ToResponse(FullyPopulatedRecord()), WebDefaults);

        Assert.DoesNotContain("businessId", json, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The stored-document metadata the purchase endpoints have always exposed. It names a file in
    /// protected storage rather than a physical server path (AGENTS.md § Files and attachments), so
    /// it stays on the wire exactly as before.
    /// </summary>
    [Fact]
    public void Response_keeps_the_stored_document_metadata_the_entity_exposed()
    {
        var json = JsonSerializer.Serialize(
            PurchaseResponseMapper.ToResponse(FullyPopulatedRecord()), WebDefaults);

        using var document = JsonDocument.Parse(json);
        Assert.Equal("scan.jpg", document.RootElement.GetProperty("fileName").GetString());
        Assert.Equal("abc-def.jpg", document.RootElement.GetProperty("storedFileName").GetString());
        Assert.Equal("image/jpeg", document.RootElement.GetProperty("contentType").GetString());
        Assert.Equal(2048, document.RootElement.GetProperty("fileSizeBytes").GetInt64());
    }
}
