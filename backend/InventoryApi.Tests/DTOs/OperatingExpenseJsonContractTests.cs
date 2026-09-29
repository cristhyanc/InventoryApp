using System.Text.Json;
using InventoryApi.DTOs;
using InventoryApi.Models;
using Xunit;

namespace InventoryApi.Tests.DTOs;

/// <summary>
/// Locks the wire contract of the "/api/operating-expenses" single-record endpoints now that
/// <see cref="OperatingExpenseResponse"/> replaced the EF <c>OperatingExpense</c> entity the
/// controller used to serialize directly: same keys, same order, same nested supplier shape, and
/// still no "businessId" or stored-file-name field.
/// </summary>
public class OperatingExpenseJsonContractTests
{
    private static readonly JsonSerializerOptions WebDefaults = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Response_serializes_with_the_same_keys_the_entity_used_to_expose()
    {
        var response = new OperatingExpenseResponse(
            7,
            new DateTime(2026, 3, 1),
            OperatingExpenseCategory.Insurance,
            "Monthly insurance",
            10m,
            1m,
            11m,
            3,
            new SupplierResponse(3, "Acme", null, null, null, null),
            null,
            null,
            "invoice.pdf",
            "application/pdf",
            12345,
            null,
            null,
            null,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));

        var json = JsonSerializer.Serialize(response, WebDefaults);

        using var document = JsonDocument.Parse(json);
        Assert.Equal(
            new[]
            {
                "id", "expenseDate", "category", "description", "amountExGst", "gstAmount", "totalAmount",
                "supplierId", "supplier", "siteId", "machineId", "attachmentFileName", "attachmentContentType",
                "attachmentFileSizeBytes", "servicePeriodStart", "servicePeriodEnd", "notes", "createdAt", "updatedAt",
            },
            document.RootElement.EnumerateObject().Select(property => property.Name));

        Assert.Equal("Acme", document.RootElement.GetProperty("supplier").GetProperty("name").GetString());
    }

    [Fact]
    public void Response_omits_a_stored_file_name_and_business_id()
    {
        var response = new OperatingExpenseResponse(
            7, new DateTime(2026, 3, 1), OperatingExpenseCategory.Insurance, "Insurance", 10m, 1m, 11m,
            null, null, null, null, null, null, null, null, null, null,
            new DateTime(2026, 1, 1), new DateTime(2026, 1, 1));

        var json = JsonSerializer.Serialize(response, WebDefaults);

        Assert.DoesNotContain("storedFileName", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("businessId", json, StringComparison.OrdinalIgnoreCase);
    }
}
