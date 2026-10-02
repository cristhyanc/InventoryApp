namespace Inventory.Domain.SupplierOrders;

/// <summary>A candidate supplier-order line's product/quantity shape, before persistence.</summary>
public readonly record struct SupplierOrderLineCandidate(long ProductId, decimal QuantityOrdered);

/// <summary>
/// Validates a new supplier order's lines: at least one line, every ordered quantity a positive
/// whole unit, and every line referencing a distinct product. Mirrors the former
/// <c>InventoryApi.Services.SupplierOrderService.Create</c> inline checks exactly; whether the
/// referenced products actually exist requires a persistence lookup and stays with the
/// Application use case/store.
/// </summary>
public static class SupplierOrderLineValidationPolicy
{
    public const string InvalidQuantityMessage = "An order must include at least one positive whole-unit quantity.";
    public const string DuplicateOrUnknownProductMessage = "Each order line must reference a distinct existing product.";

    public static bool HasInvalidQuantity(IReadOnlyCollection<SupplierOrderLineCandidate> lines) =>
        lines.Count == 0 || lines.Any(line => line.QuantityOrdered <= 0 || line.QuantityOrdered != decimal.Truncate(line.QuantityOrdered));

    public static bool HasDuplicateProduct(IReadOnlyCollection<SupplierOrderLineCandidate> lines) =>
        lines.Select(line => line.ProductId).Distinct().Count() != lines.Count;
}
