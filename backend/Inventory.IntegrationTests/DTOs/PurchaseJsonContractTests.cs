using System.Text.Json;
using InventoryApi.DTOs;
using Xunit;

namespace InventoryApi.Tests.DTOs;

/// <summary>
/// Locks the canonical wire contract of the "/api/purchases" endpoints (issue #127): the
/// business record serializes under the "purchase" key, not the legacy "receipt" key. Issue #304
/// replaced the EF <c>Purchase</c> entity in that key with the API-owned
/// <see cref="PurchaseResponse"/>; the envelope itself is unchanged, and
/// <see cref="PurchaseResponseJsonContractTests"/> pins the bytes inside the key against the entity
/// shape they replaced.
///
/// Issue #431 added the third "gst" member - the saved purchase's input GST and unresolved
/// components, so the purchase pages display the server's figures instead of calculating GST
/// themselves. It is additive: "purchase" and "validation" keep their names, order and values.
/// </summary>
public class PurchaseJsonContractTests
{
    private static readonly JsonSerializerOptions WebDefaults = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Response_serializes_with_the_purchase_and_validation_keys()
    {
        var purchase = new PurchaseResponse
        {
            Id = 7,
            Title = "Weekly restock",
            PurchaseDate = new DateTime(2026, 3, 1),
            FileName = "scan.jpg",
            StoredFileName = "abc.jpg",
            ContentType = "image/jpeg",
            FileSizeBytes = 3,
            CreatedAt = new DateTime(2026, 3, 1),
            Items =
            [
                new PurchaseItemResponse { Id = 1, ReceiptId = 7, ProductId = 3, Quantity = 2m, UnitCost = 1.5m }
            ]
        };
        var validation = new PurchaseValidationDto(true, 3m, 3m, 0.5m);
        var gst = new PurchaseGstSummaryDto(0.27m, 1, 1.5m);
        var response = new PurchaseResponseDto(purchase, validation, gst);

        var json = JsonSerializer.Serialize(response, WebDefaults);
        using var document = JsonDocument.Parse(json);

        Assert.Equal(
            new[] { "purchase", "validation", "gst" },
            document.RootElement.EnumerateObject().Select(property => property.Name));

        var purchaseElement = document.RootElement.GetProperty("purchase");
        Assert.Equal(7, purchaseElement.GetProperty("id").GetInt32());
        Assert.Equal("Weekly restock", purchaseElement.GetProperty("title").GetString());

        var itemElement = purchaseElement.GetProperty("items")[0];
        Assert.Equal(7, itemElement.GetProperty("receiptId").GetInt32());
        Assert.Equal(3, itemElement.GetProperty("productId").GetInt32());

        var gstElement = document.RootElement.GetProperty("gst");
        Assert.Equal(0.27m, gstElement.GetProperty("inputGst").GetDecimal());
        Assert.Equal(1, gstElement.GetProperty("unresolvedComponentCount").GetInt32());
        Assert.Equal(1.5m, gstElement.GetProperty("unresolvedAmount").GetDecimal());
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
