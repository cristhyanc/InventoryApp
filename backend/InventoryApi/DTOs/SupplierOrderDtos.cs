using Inventory.Domain.SupplierOrders;

namespace InventoryApi.DTOs;

public record SupplierOrderLineCreateDto(long ProductId, decimal QuantityOrdered, decimal? UnitPrice = null, string? Notes = null);

public record SupplierOrderCreateDto(
    int SupplierId,
    DateTime OrderDate,
    DateTime? ExpectedDate,
    string? Reference,
    string? Notes,
    IReadOnlyList<SupplierOrderLineCreateDto> Lines);

/// <summary>
/// The wire shape of a supplier order on the "/api/supplierorders" endpoints (issue #304),
/// replacing the EF <c>InventoryApi.Models.SupplierOrder</c> entity the retired
/// <c>SupplierOrderService</c> rebuilt for them. Key names, order, nesting and values match that
/// entity's serializable surface exactly, so this is not a contract change;
/// <c>InventoryApi.Tests.DTOs.SupplierOrderJsonContractTests</c> compares the serialized bytes of
/// both.
///
/// <see cref="Status"/> carries the Domain enum, which mirrors
/// <c>InventoryApi.Models.SupplierOrderStatus</c> member for member, so the serialized numeric
/// value clients already switch on is unchanged. The owning business never travels on the wire
/// (AGENTS.md § Tenant ownership and data isolation).
/// </summary>
public sealed record SupplierOrderResponse
{
    public required int Id { get; init; }

    public int? SupplierId { get; init; }
    public SupplierResponse? Supplier { get; init; }

    public required DateTime OrderDate { get; init; }
    public DateTime? ExpectedDate { get; init; }
    public string? Reference { get; init; }
    public string? Notes { get; init; }
    public required SupplierOrderStatus Status { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required DateTime UpdatedAt { get; init; }

    public IReadOnlyList<SupplierOrderLineResponse> Lines { get; init; } = [];
}

/// <summary>
/// One supplier-order line as the supplier-order endpoints serialize it. Matches the serializable
/// surface of the <c>InventoryApi.Models.SupplierOrderLine</c> entity it replaced: the owning
/// business, the parent-order back-reference and the receipt allocations stay off the wire.
/// </summary>
public sealed record SupplierOrderLineResponse
{
    public required int Id { get; init; }
    public required int SupplierOrderId { get; init; }
    public required long ProductId { get; init; }
    public required ProductResponse Product { get; init; }
    public required decimal QuantityOrdered { get; init; }
    public required decimal QuantityReceived { get; init; }
    public decimal? UnitPrice { get; init; }
    public string? Notes { get; init; }

    /// <summary>
    /// The parent order's fulfillment state, needed only to derive
    /// <see cref="OutstandingQuantity"/>. It is not serialized: a client reads the order's own
    /// <see cref="SupplierOrderResponse.Status"/>, exactly as it did when the entity's computed
    /// property reached its <c>[JsonIgnore]</c>d parent navigation for the same fact.
    ///
    /// It cannot be <c>required</c>, because System.Text.Json rejects a required property it is
    /// also told to ignore. <c>SupplierOrderResponseMapper</c> is the only thing that builds a line
    /// and always supplies it from the order being mapped;
    /// <c>InventoryApi.Tests.DTOs.SupplierOrderJsonContractTests</c> asserts the serialized result
    /// for every status, so a mapper that stopped supplying it would fail there.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public SupplierOrderStatus OrderStatus { get; init; }

    /// <summary>
    /// How much of this line is still expected to arrive, derived here rather than carried as data
    /// through the one authoritative <see cref="SupplierOrderLineOutstandingPolicy"/> the entity
    /// now also uses, per AGENTS.md's "one authoritative calculation must feed all presentations".
    /// </summary>
    public decimal OutstandingQuantity =>
        SupplierOrderLineOutstandingPolicy.Outstanding(OrderStatus, QuantityOrdered, QuantityReceived);
}
