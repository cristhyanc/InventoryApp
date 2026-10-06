using Inventory.Application.Expenses;

namespace InventoryApi.Tests.Application.Expenses;

/// <summary>
/// In-memory fake of the persistence port, so Application use-case tests exercise orchestration
/// without depending on EF Core or SQLite. Supplier resolution always returns <c>null</c> here;
/// see <c>EfOperatingExpenseStoreTests</c> for that behaviour, which needs a real join.
/// </summary>
public sealed class FakeOperatingExpenseStore : IOperatingExpenseStore
{
    private readonly Dictionary<int, OperatingExpenseRecord> _records = [];
    private int _nextId = 1;

    public bool ThrowOnAdd { get; set; }
    public bool ThrowOnUpdate { get; set; }

    public Task<IReadOnlyList<OperatingExpenseListItem>> ListAsync(OperatingExpenseFilter filter, CancellationToken cancellationToken)
    {
        IEnumerable<OperatingExpenseRecord> query = _records.Values;
        if (filter.From.HasValue) query = query.Where(x => x.ExpenseDate >= filter.From.Value.Date);
        if (filter.To.HasValue) query = query.Where(x => x.ExpenseDate < filter.To.Value.Date.AddDays(1));
        if (filter.Category.HasValue) query = query.Where(x => x.Category == filter.Category.Value);
        if (filter.SupplierId.HasValue) query = query.Where(x => x.SupplierId == filter.SupplierId.Value);
        if (filter.SiteId.HasValue) query = query.Where(x => x.SiteId == filter.SiteId.Value);
        if (filter.MachineId.HasValue) query = query.Where(x => x.MachineId == filter.MachineId.Value);

        IReadOnlyList<OperatingExpenseListItem> result = query
            .OrderByDescending(x => x.ExpenseDate).ThenByDescending(x => x.Id)
            .Select(x => new OperatingExpenseListItem(
                x.Id, x.ExpenseDate, x.Category, x.Description, x.AmountExGst, x.GstAmount, x.TotalAmount,
                x.SupplierId, x.Supplier?.Name, x.SiteId, x.MachineId, x.AttachmentFileName,
                x.AttachmentFileSizeBytes, x.ServicePeriodStart, x.ServicePeriodEnd, x.Notes))
            .ToList();
        return Task.FromResult(result);
    }

    public Task<OperatingExpenseRecord?> FindByIdAsync(int id, CancellationToken cancellationToken) =>
        Task.FromResult(_records.GetValueOrDefault(id));

    public Task<OperatingExpenseRecord> AddAsync(OperatingExpenseFields fields, OperatingExpenseAttachmentMetadata? attachment, CancellationToken cancellationToken)
    {
        if (ThrowOnAdd) throw new InvalidOperationException("store failure");

        var id = _nextId++;
        var record = BuildRecord(id, fields, attachment, DateTime.UtcNow, DateTime.UtcNow);
        _records[id] = record;
        return Task.FromResult(record);
    }

    public Task<OperatingExpenseRecord?> UpdateAsync(int id, OperatingExpenseFields fields, OperatingExpenseAttachmentMetadata? newAttachment, CancellationToken cancellationToken)
    {
        if (!_records.TryGetValue(id, out var existing)) return Task.FromResult<OperatingExpenseRecord?>(null);
        if (ThrowOnUpdate) throw new InvalidOperationException("store failure");

        var attachment = newAttachment ?? (existing.AttachmentStoredFileName is null
            ? null
            : new OperatingExpenseAttachmentMetadata(
                existing.AttachmentFileName!, existing.AttachmentStoredFileName!,
                existing.AttachmentContentType!, existing.AttachmentFileSizeBytes!.Value));

        var updated = BuildRecord(id, fields, attachment, existing.CreatedAt, DateTime.UtcNow);
        _records[id] = updated;
        return Task.FromResult<OperatingExpenseRecord?>(updated);
    }

    public Task<OperatingExpenseRecord?> DeleteAsync(int id, CancellationToken cancellationToken)
    {
        if (!_records.Remove(id, out var existing)) return Task.FromResult<OperatingExpenseRecord?>(null);
        return Task.FromResult<OperatingExpenseRecord?>(existing);
    }

    public void Seed(OperatingExpenseRecord record)
    {
        _records[record.Id] = record;
        _nextId = Math.Max(_nextId, record.Id + 1);
    }

    private static OperatingExpenseRecord BuildRecord(
        int id, OperatingExpenseFields fields, OperatingExpenseAttachmentMetadata? attachment,
        DateTime createdAt, DateTime updatedAt) => new(
        id, fields.ExpenseDate, fields.Category, fields.Description, fields.AmountExGst, fields.GstAmount,
        fields.TotalAmount, fields.SupplierId, null, fields.SiteId, fields.MachineId,
        attachment?.FileName, attachment?.StoredFileName, attachment?.ContentType, attachment?.FileSizeBytes,
        fields.ServicePeriodStart, fields.ServicePeriodEnd, fields.Notes, createdAt, updatedAt);
}
