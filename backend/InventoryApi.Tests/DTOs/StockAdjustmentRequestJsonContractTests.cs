using System.Text.Json;
using InventoryApi.DTOs;
using Xunit;
using DomainStock = Inventory.Domain.Stock;
using PersistenceStock = InventoryApi.Models;

namespace InventoryApi.Tests.DTOs;

/// <summary>
/// Locks the request side of the "/api/products/{productId}/stock" adjust endpoint.
///
/// <see cref="StockAdjustmentDto.Reason"/> is still
/// <c>InventoryApi.Models.StockAdjustmentReason</c>, the one deliberate, temporary compatibility
/// exception issue #305 leaves behind: the published document carries a single
/// <c>StockAdjustmentReason</c> component derived from that CLR enum, reached from the request body,
/// from the pinned <c>StockAdjustment</c> response and from the legacy <c>Product</c> component the
/// Swagger compatibility boundary regenerates, so an API-owned enum of the same simple name cannot
/// exist beside it without changing the published contract issue #305 must preserve
/// (<c>InventoryApi.Tests.Swagger.StockAndExpenseSchemaContractTests</c> compares that component
/// with the base branch's and reproduces the collision).
///
/// While the exception stands, the two things a client depends on are pinned here: the numeric
/// values a request body binds from, and the member-for-member agreement between the persistence
/// enum the DTO carries and the Domain enum the controller casts it to. A member added to,
/// renamed in or reordered in one of them would silently remap what a client asked for into a
/// different stock movement.
/// </summary>
public class StockAdjustmentRequestJsonContractTests
{
    private static readonly JsonSerializerOptions WebDefaults = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("0", PersistenceStock.StockAdjustmentReason.Restock)]
    [InlineData("1", PersistenceStock.StockAdjustmentReason.Sale)]
    [InlineData("2", PersistenceStock.StockAdjustmentReason.Damaged)]
    [InlineData("3", PersistenceStock.StockAdjustmentReason.Expired)]
    [InlineData("4", PersistenceStock.StockAdjustmentReason.Correction)]
    [InlineData("5", PersistenceStock.StockAdjustmentReason.MachineRefill)]
    public void Request_binds_the_reason_from_the_values_clients_send(
        string reasonJson, PersistenceStock.StockAdjustmentReason expected)
    {
        var json = $$"""
        {
          "quantityChange": 3,
          "reason": {{reasonJson}},
          "notes": "note",
          "machineId": null,
          "eatBefore": null,
          "unitCost": 1.25
        }
        """;

        var dto = JsonSerializer.Deserialize<StockAdjustmentDto>(json, WebDefaults);

        Assert.Equal(expected, dto!.Reason);
    }

    /// <summary>
    /// The request's remaining fields, bound from a body that carries only what the stock dialog
    /// sends: <c>unitCost</c> has a default, every other optional value is an explicit null.
    /// </summary>
    [Fact]
    public void Request_binds_the_rest_of_the_body_as_it_always_did()
    {
        var json = """
        {
          "quantityChange": -2,
          "reason": 2,
          "notes": "spoiled",
          "machineId": 9,
          "eatBefore": "2026-06-30T00:00:00"
        }
        """;

        var dto = JsonSerializer.Deserialize<StockAdjustmentDto>(json, WebDefaults)!;

        Assert.Equal(-2, dto.QuantityChange);
        Assert.Equal(PersistenceStock.StockAdjustmentReason.Damaged, dto.Reason);
        Assert.Equal("spoiled", dto.Notes);
        Assert.Equal(9, dto.MachineId);
        Assert.Equal(new DateTime(2026, 6, 30), dto.EatBefore);
        Assert.Null(dto.UnitCost);
    }

    /// <summary>
    /// The controller converts the bound reason to <c>Inventory.Domain.Stock.StockAdjustmentReason</c>
    /// by a plain cast, and the response mapper casts the Domain reason/source back, so the two
    /// vocabularies must name the same members with the same numeric values. The literal values are
    /// asserted as well, so a coordinated renumbering of both still fails: they are what clients
    /// send, what the published enum components describe and what the
    /// <c>StockAdjustments.Reason</c>/<c>Source</c> columns store.
    /// </summary>
    [Fact]
    public void Reason_and_source_vocabularies_mirror_the_domain_enums()
    {
        static (string Name, int Value)[] Members<TEnum>() where TEnum : struct, Enum =>
            Enum.GetValues<TEnum>()
                .Select(member => (member.ToString()!, Convert.ToInt32(member, System.Globalization.CultureInfo.InvariantCulture)))
                .OrderBy(member => member.Item2)
                .ToArray();

        Assert.Equal(
            Members<DomainStock.StockAdjustmentReason>(),
            Members<PersistenceStock.StockAdjustmentReason>());
        Assert.Equal(
            Members<DomainStock.StockAdjustmentSource>(),
            Members<PersistenceStock.StockAdjustmentSource>());

        Assert.Equal(
            new[]
            {
                ("Restock", 0), ("Sale", 1), ("Damaged", 2), ("Expired", 3), ("Correction", 4),
                ("MachineRefill", 5),
            },
            Members<PersistenceStock.StockAdjustmentReason>());
        Assert.Equal(
            new[] { ("Manual", 0), ("Nayax", 1) },
            Members<PersistenceStock.StockAdjustmentSource>());
    }
}
