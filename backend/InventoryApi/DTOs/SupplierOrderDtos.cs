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
/// One line of the purchase an operator has entered but not yet saved, as the completion preview is
/// asked about it (issue #974). Only the product and the quantity: the preview derives everything
/// else from the stored order, and nothing here is persisted.
/// </summary>
public record SupplierOrderCompletionPreviewLineDto(long ProductId, decimal Quantity);

/// <summary>
/// The request the purchase form makes before offering the "keep order open / complete order" choice
/// (issue #974). The supplier and the purchase date are the ones on the form, because they decide
/// which outstanding order lines the save would allocate against; the order itself comes from the
/// route.
/// </summary>
public record SupplierOrderCompletionPreviewDto(
    int? SupplierId,
    DateTime? PurchaseDate,
    IReadOnlyList<SupplierOrderCompletionPreviewLineDto> Lines);

/// <summary>
/// What the order would still be owed once the entered purchase is applied (issue #974), together
/// with the fingerprint the save compares its own authoritative read against.
///
/// <para>Every quantity here is the server's: the allocation is
/// <c>Inventory.Domain.SupplierOrders.SupplierOrderFulfillmentAllocationPolicy</c>'s - the purchase
/// write's own - and the remaining balance
/// <c>Inventory.Domain.SupplierOrders.SupplierOrderLineOutstandingPolicy</c>'s. The frontend formats
/// them and calculates nothing.</para>
/// </summary>
public record SupplierOrderCompletionPreviewResponse(
    int SupplierOrderId,
    SupplierOrderStatus Status,
    bool EligibleForCompletion,
    bool HasOutstandingBalance,
    string DecisionFingerprint,
    IReadOnlyList<SupplierOrderOutstandingLineResponse> OutstandingLines);

/// <summary>One product the order would still be owed, with the quantity and the unit it was ordered in.</summary>
public record SupplierOrderOutstandingLineResponse(
    int SupplierOrderLineId,
    long ProductId,
    string ProductName,
    string? Unit,
    decimal QuantityOrdered,
    decimal QuantityAlreadyReceived,
    decimal QuantityOnThisPurchase,
    decimal OutstandingQuantity);

/// <summary>
/// The wire shape of a supplier order on the "/api/supplierorders" endpoints (issue #304),
/// replacing the EF <c>Inventory.Infrastructure.Models.SupplierOrder</c> entity the retired
/// <c>SupplierOrderService</c> rebuilt for them. Key names, order, nesting and values match that
/// entity's serializable surface exactly, so this is not a contract change;
/// <c>InventoryApi.Tests.DTOs.SupplierOrderJsonContractTests</c> compares the serialized bytes of
/// both.
///
/// <see cref="Status"/> carries the Domain enum, which mirrors
/// <c>Inventory.Infrastructure.Models.SupplierOrderStatus</c> member for member, so the serialized numeric
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

    /// <summary>
    /// When an operator decided no more of this order would arrive (issue #974), and <c>null</c> on
    /// every order carrying no such decision. Additive: a client that reads only the keys it already
    /// knew is unaffected. The actor who decided is stored with the order and deliberately not
    /// published - an Entra <c>(tid, oid)</c> pair identifies a person.
    /// </summary>
    public DateTime? CompletedWithUndeliveredAt { get; init; }

    public IReadOnlyList<SupplierOrderLineResponse> Lines { get; init; } = [];
}

/// <summary>
/// One supplier-order line as the supplier-order endpoints serialize it. Matches the serializable
/// surface of the <c>Inventory.Infrastructure.Models.SupplierOrderLine</c> entity it replaced: the owning
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

    /// <summary>
    /// How much of this line was ordered and will never be delivered (issue #974), from the same
    /// authoritative policy. It is <c>0</c> on every order that was not explicitly completed with an
    /// undelivered balance, so "still expected" and "never came" stay two separate answers rather
    /// than one number a client has to interpret - and neither is ever derived in a browser.
    /// </summary>
    public decimal UndeliveredQuantity =>
        SupplierOrderLineOutstandingPolicy.Undelivered(OrderStatus, QuantityOrdered, QuantityReceived);
}
