using Inventory.Application.SupplierOrders;
using InventoryApi.Adapters.Mapping;
using InventoryApi.DTOs;
using InventoryApi.Models;
using InventoryApi.Services.Interfaces;

namespace InventoryApi.Services;

/// <summary>
/// A transitional delegator only (issue #281, the same shape #240 left the <c>ProductService</c>
/// issue #303 has since deleted): every method maps the <see cref="ISupplierOrderService"/> request onto the migrated
/// <see cref="Inventory.Application.SupplierOrders"/> use case that owns it, and maps the result
/// back to the unchanged <see cref="SupplierOrder"/> API response through
/// <see cref="SupplierOrderResponseMapper"/>. It holds no <c>AppDbContext</c>, no query, and no
/// rule of its own. Deleting it, and with it <see cref="ISupplierOrderService"/>, is tracked by
/// issue #153.
/// </summary>
public class SupplierOrderService : ISupplierOrderService
{
    private readonly ListActiveSupplierOrders _listActiveSupplierOrders;
    private readonly GetSupplierOrder _getSupplierOrder;
    private readonly CreateSupplierOrder _createSupplierOrder;
    private readonly CancelSupplierOrder _cancelSupplierOrder;

    public SupplierOrderService(
        ListActiveSupplierOrders listActiveSupplierOrders,
        GetSupplierOrder getSupplierOrder,
        CreateSupplierOrder createSupplierOrder,
        CancelSupplierOrder cancelSupplierOrder)
    {
        _listActiveSupplierOrders = listActiveSupplierOrders;
        _getSupplierOrder = getSupplierOrder;
        _createSupplierOrder = createSupplierOrder;
        _cancelSupplierOrder = cancelSupplierOrder;
    }

    public async Task<IEnumerable<SupplierOrder>> GetActive()
    {
        var records = await _listActiveSupplierOrders.Handle(CancellationToken.None);
        return records.Select(SupplierOrderResponseMapper.ToSupplierOrder).ToList();
    }

    public async Task<SupplierOrder?> GetById(int id)
    {
        var record = await _getSupplierOrder.Handle(id, CancellationToken.None);
        return record is null ? null : SupplierOrderResponseMapper.ToSupplierOrder(record);
    }

    public async Task<SupplierOrder?> Create(SupplierOrderCreateDto dto)
    {
        var fields = new SupplierOrderCreateFields(
            dto.SupplierId,
            dto.OrderDate,
            dto.ExpectedDate,
            dto.Reference,
            dto.Notes,
            dto.Lines.Select(line => new SupplierOrderLineInput(line.ProductId, line.QuantityOrdered, line.UnitPrice, line.Notes)).ToList());

        var record = await _createSupplierOrder.Handle(fields, CancellationToken.None);
        return record is null ? null : SupplierOrderResponseMapper.ToSupplierOrder(record);
    }

    public Task<bool> Cancel(int id) => _cancelSupplierOrder.Handle(id, CancellationToken.None);
}
