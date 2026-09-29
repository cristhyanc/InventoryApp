using Inventory.Application.Expenses;
using Inventory.Domain.Expenses;
using InventoryApi.Data;
using InventoryApi.Models;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Adapters.Persistence;

/// <summary>
/// Temporary EF Core implementation of <see cref="IOperatingExpenseStore"/>. It lives in
/// InventoryApi, not Inventory.Infrastructure, because it depends on <see cref="AppDbContext"/>
/// and <see cref="OperatingExpense"/>, which still live in InventoryApi. Move it into
/// Inventory.Infrastructure once the shared AppDbContext and persistence models relocate there.
/// </summary>
public sealed class EfOperatingExpenseStore : IOperatingExpenseStore
{
    private readonly AppDbContext _db;

    public EfOperatingExpenseStore(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<OperatingExpenseListItem>> ListAsync(OperatingExpenseFilter filter, CancellationToken cancellationToken)
    {
        var query = _db.OperatingExpenses.AsNoTracking().Include(x => x.Supplier).AsQueryable();
        if (filter.From.HasValue) query = query.Where(x => x.ExpenseDate >= filter.From.Value.Date);
        if (filter.To.HasValue) query = query.Where(x => x.ExpenseDate < filter.To.Value.Date.AddDays(1));
        if (filter.Category.HasValue) query = query.Where(x => x.Category == (OperatingExpenseCategory)filter.Category.Value);
        if (filter.SupplierId.HasValue) query = query.Where(x => x.SupplierId == filter.SupplierId.Value);
        if (filter.SiteId.HasValue) query = query.Where(x => x.SiteId == filter.SiteId.Value);
        if (filter.MachineId.HasValue) query = query.Where(x => x.MachineId == filter.MachineId.Value);

        return await query.OrderByDescending(x => x.ExpenseDate).ThenByDescending(x => x.Id)
            .Select(x => new OperatingExpenseListItem(
                x.Id, x.ExpenseDate, (ExpenseCategory)x.Category, x.Description,
                x.AmountExGst, x.GstAmount, x.TotalAmount, x.SupplierId,
                x.Supplier == null ? null : x.Supplier.Name, x.SiteId, x.MachineId,
                x.AttachmentFileName, x.AttachmentFileSizeBytes,
                x.ServicePeriodStart, x.ServicePeriodEnd, x.Notes))
            .ToListAsync(cancellationToken);
    }

    public async Task<OperatingExpenseRecord?> FindByIdAsync(int id, CancellationToken cancellationToken)
    {
        var expense = await _db.OperatingExpenses.AsNoTracking().Include(x => x.Supplier)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        return expense is null ? null : ToRecord(expense);
    }

    public async Task<OperatingExpenseRecord> AddAsync(OperatingExpenseFields fields, OperatingExpenseAttachmentMetadata? attachment, CancellationToken cancellationToken)
    {
        var entity = new OperatingExpense();
        Apply(entity, fields);
        ApplyAttachment(entity, attachment);
        _db.OperatingExpenses.Add(entity);
        await _db.SaveChangesAsync(cancellationToken);
        return ToRecord(entity);
    }

    public async Task<OperatingExpenseRecord?> UpdateAsync(int id, OperatingExpenseFields fields, OperatingExpenseAttachmentMetadata? newAttachment, CancellationToken cancellationToken)
    {
        var entity = await _db.OperatingExpenses.FindAsync(new object[] { id }, cancellationToken);
        if (entity is null) return null;

        Apply(entity, fields);
        ApplyAttachment(entity, newAttachment);
        entity.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        // Loaded explicitly, against the final SupplierId, right before the record is returned -
        // Update may itself have just changed SupplierId, and lazy loading is never used here.
        if (entity.SupplierId.HasValue)
            await _db.Entry(entity).Reference(e => e.Supplier).LoadAsync(cancellationToken);

        return ToRecord(entity);
    }

    public async Task<OperatingExpenseRecord?> DeleteAsync(int id, CancellationToken cancellationToken)
    {
        var entity = await _db.OperatingExpenses.FindAsync(new object[] { id }, cancellationToken);
        if (entity is null) return null;

        _db.OperatingExpenses.Remove(entity);
        await _db.SaveChangesAsync(cancellationToken);
        return ToRecord(entity);
    }

    private static void Apply(OperatingExpense entity, OperatingExpenseFields fields)
    {
        entity.ExpenseDate = fields.ExpenseDate;
        entity.Category = (OperatingExpenseCategory)fields.Category;
        entity.Description = fields.Description;
        entity.AmountExGst = fields.AmountExGst;
        entity.GstAmount = fields.GstAmount;
        entity.TotalAmount = fields.TotalAmount;
        entity.SupplierId = fields.SupplierId;
        entity.SiteId = fields.SiteId;
        entity.MachineId = fields.MachineId;
        entity.ServicePeriodStart = fields.ServicePeriodStart;
        entity.ServicePeriodEnd = fields.ServicePeriodEnd;
        entity.Notes = fields.Notes;
    }

    private static void ApplyAttachment(OperatingExpense entity, OperatingExpenseAttachmentMetadata? attachment)
    {
        if (attachment is null) return;
        entity.AttachmentFileName = attachment.FileName;
        entity.AttachmentStoredFileName = attachment.StoredFileName;
        entity.AttachmentContentType = attachment.ContentType;
        entity.AttachmentFileSizeBytes = attachment.FileSizeBytes;
    }

    private static OperatingExpenseRecord ToRecord(OperatingExpense entity) => new(
        entity.Id, entity.ExpenseDate, (ExpenseCategory)entity.Category, entity.Description,
        entity.AmountExGst, entity.GstAmount, entity.TotalAmount, entity.SupplierId,
        entity.Supplier is null
            ? null
            : new OperatingExpenseSupplier(
                entity.Supplier.Id, entity.Supplier.Name, entity.Supplier.ContactName,
                entity.Supplier.Phone, entity.Supplier.Email, entity.Supplier.Address),
        entity.SiteId, entity.MachineId, entity.AttachmentFileName, entity.AttachmentStoredFileName,
        entity.AttachmentContentType, entity.AttachmentFileSizeBytes, entity.ServicePeriodStart,
        entity.ServicePeriodEnd, entity.Notes, entity.CreatedAt, entity.UpdatedAt);
}
