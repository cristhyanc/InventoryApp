namespace InventoryApi.DTOs;

public record SupplierOrderLineCreateDto(long ProductId, decimal QuantityOrdered, decimal? UnitPrice = null, string? Notes = null);

public record SupplierOrderCreateDto(
    int SupplierId,
    DateTime OrderDate,
    DateTime? ExpectedDate,
    string? Reference,
    string? Notes,
    IReadOnlyList<SupplierOrderLineCreateDto> Lines);
