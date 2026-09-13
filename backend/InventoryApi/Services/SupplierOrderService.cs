using InventoryApi.Data;
using InventoryApi.DTOs;
using InventoryApi.Models;
using InventoryApi.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Services;

public class SupplierOrderService : ISupplierOrderService
{
    private readonly AppDbContext _db;

    public SupplierOrderService(AppDbContext db) => _db = db;

    public async Task<IEnumerable<SupplierOrder>> GetActive() => await _db.SupplierOrders
        .AsNoTracking()
        .Include(order => order.Supplier)
        .Include(order => order.Lines)
            .ThenInclude(line => line.Product)
        .Where(order => order.Status != SupplierOrderStatus.Cancelled && order.Status != SupplierOrderStatus.Received)
        .OrderBy(order => order.ExpectedDate ?? order.OrderDate)
        .ThenBy(order => order.Id)
        .ToListAsync();

    public async Task<SupplierOrder?> Create(SupplierOrderCreateDto dto)
    {
        if (dto.Lines.Count == 0 || dto.Lines.Any(line => line.QuantityOrdered <= 0 || line.QuantityOrdered != decimal.Truncate(line.QuantityOrdered)))
            throw new InvalidOperationException("An order must include at least one positive whole-unit quantity.");
        if (!await _db.Suppliers.AnyAsync(supplier => supplier.Id == dto.SupplierId)) return null;

        var productIds = dto.Lines.Select(line => line.ProductId).Distinct().ToList();
        if (productIds.Count != dto.Lines.Count || await _db.Products.CountAsync(product => productIds.Contains(product.Id)) != productIds.Count)
            throw new InvalidOperationException("Each order line must reference a distinct existing product.");

        var now = DateTime.UtcNow;
        var order = new SupplierOrder
        {
            SupplierId = dto.SupplierId,
            OrderDate = dto.OrderDate,
            ExpectedDate = dto.ExpectedDate,
            Reference = dto.Reference,
            Notes = dto.Notes,
            CreatedAt = now,
            UpdatedAt = now,
            Lines = dto.Lines.Select(line => new SupplierOrderLine
            {
                ProductId = line.ProductId,
                QuantityOrdered = line.QuantityOrdered,
                UnitPrice = line.UnitPrice,
                Notes = line.Notes
            }).ToList()
        };
        _db.SupplierOrders.Add(order);
        await _db.SaveChangesAsync();
        return await _db.SupplierOrders.Include(item => item.Supplier).Include(item => item.Lines).ThenInclude(line => line.Product)
            .SingleAsync(item => item.Id == order.Id);
    }

    public async Task<bool> Cancel(int id)
    {
        var order = await _db.SupplierOrders.FindAsync(id);
        if (order is null || order.Status is SupplierOrderStatus.Cancelled or SupplierOrderStatus.Received) return false;
        order.Status = SupplierOrderStatus.Cancelled;
        order.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return true;
    }
}