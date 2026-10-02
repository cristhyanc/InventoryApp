namespace Inventory.Application.SupplierOrders;

public sealed record SupplierOrderLineInput(long ProductId, decimal QuantityOrdered, decimal? UnitPrice, string? Notes);

public sealed record SupplierOrderCreateFields(
    int SupplierId,
    DateTime OrderDate,
    DateTime? ExpectedDate,
    string? Reference,
    string? Notes,
    IReadOnlyList<SupplierOrderLineInput> Lines);
