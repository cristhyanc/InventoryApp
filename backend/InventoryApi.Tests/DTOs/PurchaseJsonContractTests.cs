using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using InventoryApi.DTOs;
using InventoryApi.Models;
using Xunit;

namespace InventoryApi.Tests.DTOs;

/// <summary>
/// Locks the wire contract of the "/api/receipts" endpoints (the Purchase business record) across
/// the Receipt-to-Purchase internal rename (issue #60): renaming the CLR types must not rename the
/// JSON keys deployed frontends and bookmarks already rely on.
/// </summary>
public class PurchaseJsonContractTests
{
    private static readonly JsonSerializerOptions WebDefaults = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Response_still_serializes_with_the_receipt_and_validation_keys()
    {
        var purchase = new Purchase
        {
            Id = 7,
            Title = "Weekly restock",
            PurchaseDate = new DateTime(2026, 3, 1),
            FileName = "scan.jpg",
            StoredFileName = "abc.jpg",
            ContentType = "image/jpeg",
            Items = new List<PurchaseItem>
            {
                new() { Id = 1, ReceiptId = 7, ProductId = 3, Quantity = 2m, UnitCost = 1.5m }
            }
        };
        var validation = new PurchaseValidationDto(true, 3m, 3m, 0.5m);
        var response = new PurchaseResponseDto(purchase, validation);

        var json = JsonSerializer.Serialize(response, WebDefaults);
        using var document = JsonDocument.Parse(json);

        Assert.Equal(
            new[] { "receipt", "validation" },
            document.RootElement.EnumerateObject().Select(property => property.Name));

        var receiptElement = document.RootElement.GetProperty("receipt");
        Assert.Equal(7, receiptElement.GetProperty("id").GetInt32());
        Assert.Equal("Weekly restock", receiptElement.GetProperty("title").GetString());

        var itemElement = receiptElement.GetProperty("items")[0];
        Assert.Equal(7, itemElement.GetProperty("receiptId").GetInt32());
        Assert.Equal(3, itemElement.GetProperty("productId").GetInt32());
    }

    [Fact]
    public void Item_request_still_deserializes_the_existing_post_body()
    {
        const string json = """[{"productId":3,"quantity":2,"unitCost":1.5}]""";

        var items = JsonSerializer.Deserialize<IReadOnlyList<PurchaseItemDto>>(json, WebDefaults);

        Assert.NotNull(items);
        var item = Assert.Single(items);
        Assert.Equal(3, item.ProductId);
        Assert.Equal(2m, item.Quantity);
        Assert.Equal(1.5m, item.UnitCost);
    }
}
