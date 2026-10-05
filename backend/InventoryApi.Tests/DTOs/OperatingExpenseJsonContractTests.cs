using System.Text.Json;
using InventoryApi.DTOs;
using Xunit;

namespace InventoryApi.Tests.DTOs;

/// <summary>
/// Locks the wire contract of the "/api/operating-expenses" single-record endpoints now that
/// <see cref="OperatingExpenseResponse"/> replaced the EF <c>OperatingExpense</c> entity the
/// controller used to serialize directly: same keys, same order, same nested supplier shape, and
/// still no "businessId" or stored-file-name field.
///
/// Since issue #305 the category these DTOs carry is the API-owned
/// <see cref="OperatingExpenseCategory"/> rather than the identically named persistence enum, so
/// the HTTP boundary no longer names <c>InventoryApi.Models</c>. That is a wire contract only while
/// the three mirrored enums stay in step, which
/// <see cref="Category_vocabulary_mirrors_the_persistence_and_domain_enums"/> asserts member by
/// member.
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

    /// <summary>
    /// The API-owned category, the persistence enum stored in the <c>OperatingExpenses.Category</c>
    /// column, and the Domain enum the use cases take must name the same members with the same
    /// numeric values: the controller converts between all three by a plain cast, so a member added
    /// to one, renamed in one, or reordered in one would silently remap every stored expense and
    /// every category a client sends. Adding a category deliberately means adding it to all three,
    /// and this test is what forces that.
    /// </summary>
    [Fact]
    public void Category_vocabulary_mirrors_the_persistence_and_domain_enums()
    {
        static (string Name, int Value)[] Members<TEnum>() where TEnum : struct, Enum =>
            Enum.GetValues<TEnum>()
                .Select(member => (member.ToString()!, Convert.ToInt32(member, System.Globalization.CultureInfo.InvariantCulture)))
                .OrderBy(member => member.Item2)
                .ToArray();

        var api = Members<OperatingExpenseCategory>();

        Assert.Equal(Members<InventoryApi.Models.OperatingExpenseCategory>(), api);
        Assert.Equal(Members<Inventory.Domain.Expenses.ExpenseCategory>(), api);

        // The published values themselves, so a coordinated renumbering of all three still fails.
        Assert.Equal(
            new[]
            {
                ("NayaxMonthlyFee", 0), ("Insurance", 1), ("RepairsAndMaintenance", 2), ("Software", 3),
                ("Accounting", 4), ("PhoneInternet", 5), ("VehicleTravel", 6), ("BankFees", 7), ("Other", 8),
            },
            api);
    }

    /// <summary>
    /// Every category serializes as the numeric value clients read and the column stores, on both
    /// the single-record response and the listing row.
    /// </summary>
    [Theory]
    [InlineData(OperatingExpenseCategory.NayaxMonthlyFee, 0)]
    [InlineData(OperatingExpenseCategory.Insurance, 1)]
    [InlineData(OperatingExpenseCategory.RepairsAndMaintenance, 2)]
    [InlineData(OperatingExpenseCategory.Software, 3)]
    [InlineData(OperatingExpenseCategory.Accounting, 4)]
    [InlineData(OperatingExpenseCategory.PhoneInternet, 5)]
    [InlineData(OperatingExpenseCategory.VehicleTravel, 6)]
    [InlineData(OperatingExpenseCategory.BankFees, 7)]
    [InlineData(OperatingExpenseCategory.Other, 8)]
    public void Category_serializes_with_its_persisted_numeric_value(OperatingExpenseCategory category, int expected)
    {
        var response = new OperatingExpenseResponse(
            7, new DateTime(2026, 3, 1), category, "Expense", 10m, 1m, 11m,
            null, null, null, null, null, null, null, null, null, null,
            new DateTime(2026, 1, 1), new DateTime(2026, 1, 1));
        var row = new OperatingExpenseReportRowDto(
            7, new DateTime(2026, 3, 1), category, "Expense", 10m, 1m, 11m,
            null, null, null, null, null, null, null, null, null);

        using var responseDocument = JsonDocument.Parse(JsonSerializer.Serialize(response, WebDefaults));
        using var rowDocument = JsonDocument.Parse(JsonSerializer.Serialize(row, WebDefaults));

        Assert.Equal(expected, responseDocument.RootElement.GetProperty("category").GetInt32());
        Assert.Equal(expected, rowDocument.RootElement.GetProperty("category").GetInt32());
    }

    /// <summary>
    /// The listing row's keys, which the report endpoint and the expenses page both read. Pinned
    /// here because the category swap touched this record too.
    /// </summary>
    [Fact]
    public void Report_row_exposes_the_same_keys_in_the_same_order()
    {
        var row = new OperatingExpenseReportRowDto(
            7, new DateTime(2026, 3, 1), OperatingExpenseCategory.Software, "Expense", 10m, 1m, 11m,
            3, "Acme", 4, 5, "invoice.pdf", 12345, null, null, "note");

        var json = JsonSerializer.Serialize(row, WebDefaults);

        using var document = JsonDocument.Parse(json);
        Assert.Equal(
            new[]
            {
                "id", "expenseDate", "category", "description", "amountExGst", "gstAmount", "totalAmount",
                "supplierId", "supplierName", "siteId", "machineId", "attachmentFileName",
                "attachmentFileSizeBytes", "servicePeriodStart", "servicePeriodEnd", "notes",
            },
            document.RootElement.EnumerateObject().Select(property => property.Name));
    }

    /// <summary>
    /// The request DTO binds the same category vocabulary it always did, from the numeric values a
    /// client sends in a JSON body.
    /// </summary>
    [Theory]
    [InlineData("0", OperatingExpenseCategory.NayaxMonthlyFee)]
    [InlineData("3", OperatingExpenseCategory.Software)]
    [InlineData("8", OperatingExpenseCategory.Other)]
    public void Request_binds_the_category_from_the_values_clients_send(string categoryJson, OperatingExpenseCategory expected)
    {
        var json = $$"""
        {
          "expenseDate": "2026-03-01T00:00:00",
          "category": {{categoryJson}},
          "description": "Expense",
          "amountExGst": 10,
          "gstAmount": 1,
          "totalAmount": 11
        }
        """;

        var dto = JsonSerializer.Deserialize<OperatingExpenseDto>(json, WebDefaults);

        Assert.Equal(expected, dto!.Category);
    }
}
