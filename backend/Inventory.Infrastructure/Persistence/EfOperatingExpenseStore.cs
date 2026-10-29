using Inventory.Application.Attachments;
using Inventory.Application.Expenses;
using Inventory.Application.Time;
using Inventory.Domain.Expenses;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IOperatingExpenseStore"/>. It lives in
/// Inventory.Infrastructure beside the <see cref="AppDbContext"/> and the
/// <see cref="OperatingExpense"/> entity it reads, which moved there in issue #307; this adapter
/// followed them in issue #309 (Persistence 8/8 of #153).
///
/// <para>Since issue #780 the machine it stores, returns and filters on is the business's own
/// machine row (<c>Machines.Id</c>) rather than the provider's machine number. Nothing here
/// translates it - the value travels unchanged from the use case to the column and back - and the
/// check that it is a machine of this business belongs to the create/update use cases, with the
/// <c>(BusinessId, MachineId)</c> foreign key as the structural backstop.</para>
/// </summary>
public sealed class EfOperatingExpenseStore : IOperatingExpenseStore
{
    private readonly AppDbContext _db;
    private readonly IClock _clock;

    public EfOperatingExpenseStore(AppDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
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

    public async Task<OperatingExpenseRecord> AddAsync(
        OperatingExpenseFields fields,
        OperatingExpenseAttachmentMetadata? attachment,
        AttachmentUploadReservation? attachmentReservation,
        CancellationToken cancellationToken)
    {
        // The write lock only matters when there is a reservation to consume (issue #968): the
        // reservation's state and the row that replaces it have to be read and written as one
        // operation, and a deferred transaction takes the lock after the read. An expense with no
        // attachment, and every expense while no plan is enforced, is the save it has always been.
        await using var transaction = attachmentReservation is null
            ? null
            : await SqliteWriteLockTransaction.BeginAsync(_db, cancellationToken);

        var entity = new OperatingExpense();
        Apply(entity, fields);
        ApplyAttachment(entity, attachment);

        if (attachmentReservation is not null)
        {
            // A new expense has no attachment to credit and no version to be stale against.
            await AttachmentQuotaCommit.ConsumeAsync(
                _db, _clock, attachmentReservation, null, null, cancellationToken);
        }

        _db.OperatingExpenses.Add(entity);
        await _db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return ToRecord(entity);
    }

    public async Task<OperatingExpenseRecord?> UpdateAsync(
        int id,
        OperatingExpenseFields fields,
        OperatingExpenseAttachmentMetadata? newAttachment,
        AttachmentUploadReservation? attachmentReservation,
        CancellationToken cancellationToken)
    {
        // Same reasoning as the insert, and one more: the attachment version this write is allowed
        // to move on is read inside this transaction, so two replacements of one attachment are
        // serialised and the second reads the first one's committed version and is refused.
        await using var transaction = attachmentReservation is null
            ? null
            : await SqliteWriteLockTransaction.BeginAsync(_db, cancellationToken);

        var entity = await _db.OperatingExpenses.FindAsync(new object[] { id }, cancellationToken);
        if (entity is null) return null;

        if (attachmentReservation is not null)
        {
            await AttachmentQuotaCommit.ConsumeAsync(
                _db,
                _clock,
                attachmentReservation,
                entity.AttachmentStoredFileName is null ? null : entity.AttachmentFileSizeBytes,
                entity.AttachmentVersion,
                cancellationToken);
        }

        Apply(entity, fields);
        ApplyAttachment(entity, newAttachment);
        entity.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);

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

        // Every write that points the expense at a document moves its attachment version on
        // (issue #968), in the same transaction as the document itself. That is what a competing
        // replacement's commit compares against, so two of them cannot both take credit for the
        // file this one just replaced.
        entity.AttachmentVersion++;
    }

    private static OperatingExpenseRecord ToRecord(OperatingExpense entity) => new(
        entity.Id, entity.ExpenseDate, (ExpenseCategory)entity.Category, entity.Description,
        entity.AmountExGst, entity.GstAmount, entity.TotalAmount, entity.SupplierId,
        entity.Supplier is null
            ? null
            : new OperatingExpenseSupplier(
                entity.Supplier.Id, entity.Supplier.Name, entity.Supplier.ContactName,
                entity.Supplier.Phone, entity.Supplier.Email, entity.Supplier.Address, entity.Supplier.Url),
        entity.SiteId, entity.MachineId, entity.AttachmentFileName, entity.AttachmentStoredFileName,
        entity.AttachmentContentType, entity.AttachmentFileSizeBytes, entity.ServicePeriodStart,
        entity.ServicePeriodEnd, entity.Notes, entity.CreatedAt, entity.UpdatedAt);
}
