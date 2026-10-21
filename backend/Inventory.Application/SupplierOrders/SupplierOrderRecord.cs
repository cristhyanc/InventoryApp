using Inventory.Domain.SupplierOrders;

namespace Inventory.Application.SupplierOrders;

/// <summary>One supplier order as the Application layer sees it, with its supplier detail and lines.</summary>
public sealed record SupplierOrderRecord(
    int Id,
    int BusinessId,
    int? SupplierId,
    SupplierOrderSupplierRecord? Supplier,
    DateTime OrderDate,
    DateTime? ExpectedDate,
    string? Reference,
    string? Notes,
    SupplierOrderStatus Status,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    IReadOnlyList<SupplierOrderLineRecord> Lines);

/// <summary>The supplier detail carried on a supplier order, as the Application layer sees it.</summary>
public sealed record SupplierOrderSupplierRecord(
    int Id, string Name, string? ContactName, string? Phone, string? Email, string? Address);

/// <summary>One persisted supplier-order line, with its product's catalogue snapshot.</summary>
public sealed record SupplierOrderLineRecord(
    int Id,
    int SupplierOrderId,
    long ProductId,
    SupplierOrderProductSummaryRecord Product,
    decimal QuantityOrdered,
    decimal QuantityReceived,
    decimal? UnitPrice,
    string? Notes);

/// <summary>
/// The catalogue snapshot a supplier-order line's product navigation carries, mirroring only the
/// persisted scalar fields the former <c>Include(line => line.Product)</c> query loaded.
///
/// <para>A product's supplier is not one of them since issue #787 (A1 of #786): a product carries
/// none, and the order's own supplier - the one a person chose when placing it - is on
/// <see cref="SupplierOrderRecord"/> itself. A product's selling price is not one of them since
/// issue #792 (A2 of #786); the line's own agreed <c>UnitPrice</c> on
/// <see cref="SupplierOrderLineRecord"/> is a different value and is untouched.</para>
/// </summary>
public sealed record SupplierOrderProductSummaryRecord(
    long Id,
    string Name,
    string? Sku,
    string? Description,
    decimal AverageUnitCost,
    int? CostingQuantity,
    decimal? InventoryValue,
    int QuantityInStock,
    int LowStockThreshold,
    int RestockTo,
    string? Unit,
    bool IsActive,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    long? CategoryId);
