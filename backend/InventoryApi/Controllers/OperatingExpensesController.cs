using Inventory.Application.Documents;
using InventoryApi.Data;
using InventoryApi.DTOs;
using InventoryApi.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

[ApiController]
[Route("api/operating-expenses")]
[Authorize]
[RequiredScope("access_as_user")]
public sealed class OperatingExpensesController : ControllerBase
{
    private const decimal GstTolerance = 0.02m;
    private const long MaxAttachmentFileSizeBytes = 10 * 1024 * 1024;
    private static readonly string[] AllowedAttachmentExtensions = { ".jpg", ".jpeg", ".png", ".pdf", ".webp", ".heic" };
    private const DocumentCategory AttachmentCategory = DocumentCategory.ExpenseAttachment;
    private readonly AppDbContext _db;
    private readonly IDocumentStorage _documents;

    public OperatingExpensesController(AppDbContext db, IDocumentStorage documents)
    {
        _db = db;
        _documents = documents;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<OperatingExpenseReportRowDto>>> Get(
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] OperatingExpenseCategory? category = null,
        [FromQuery] int? supplierId = null,
        [FromQuery] long? siteId = null,
        [FromQuery] long? machineId = null,
        CancellationToken cancellationToken = default)
    {
        var query = _db.OperatingExpenses.AsNoTracking().Include(x => x.Supplier).AsQueryable();
        if (from.HasValue) query = query.Where(x => x.ExpenseDate >= from.Value.Date);
        if (to.HasValue) query = query.Where(x => x.ExpenseDate < to.Value.Date.AddDays(1));
        if (category.HasValue) query = query.Where(x => x.Category == category.Value);
        if (supplierId.HasValue) query = query.Where(x => x.SupplierId == supplierId.Value);
        if (siteId.HasValue) query = query.Where(x => x.SiteId == siteId.Value);
        if (machineId.HasValue) query = query.Where(x => x.MachineId == machineId.Value);
        var rows = await query.OrderByDescending(x => x.ExpenseDate).ThenByDescending(x => x.Id)
            .Select(x => new OperatingExpenseReportRowDto(x.Id, x.ExpenseDate, x.Category, x.Description,
                x.AmountExGst, x.GstAmount, x.TotalAmount, x.SupplierId, x.Supplier == null ? null : x.Supplier.Name,
                x.SiteId, x.MachineId, x.AttachmentFileName, x.AttachmentFileSizeBytes,
                x.ServicePeriodStart, x.ServicePeriodEnd, x.Notes))
            .ToListAsync(cancellationToken);
        return Ok(rows);
    }

    [HttpPost]
    public async Task<ActionResult<OperatingExpense>> Create(OperatingExpenseDto dto, CancellationToken cancellationToken)
    {
        var validation = Validate(dto);
        if (validation is not null) return BadRequest(validation);
        var expense = FromDto(dto);
        _db.OperatingExpenses.Add(expense);
        await _db.SaveChangesAsync(cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = expense.Id }, expense);
    }

    [HttpPost]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxAttachmentFileSizeBytes)]
    public async Task<ActionResult<OperatingExpense>> CreateWithAttachment(
        [FromForm] OperatingExpenseDto dto,
        [FromForm(Name = "attachment")] IFormFile? attachment,
        CancellationToken cancellationToken)
    {
        var validation = Validate(dto);
        if (validation is not null) return BadRequest(validation);

        var expense = FromDto(dto);
        string? storedAttachmentName = null;
        try
        {
            storedAttachmentName = await SaveAttachmentAsync(expense, attachment, cancellationToken);
            _db.OperatingExpenses.Add(expense);
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            await DeleteAttachmentAsync(storedAttachmentName, cancellationToken);
            return BadRequest(ex.Message);
        }
        catch
        {
            await DeleteAttachmentAsync(storedAttachmentName, cancellationToken);
            throw;
        }

        return CreatedAtAction(nameof(GetById), new { id = expense.Id }, expense);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<OperatingExpense>> GetById(int id, CancellationToken cancellationToken)
    {
        var expense = await _db.OperatingExpenses.AsNoTracking().Include(x => x.Supplier).SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        return expense is null ? NotFound() : Ok(expense);
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<OperatingExpense>> Update(int id, OperatingExpenseDto dto, CancellationToken cancellationToken)
    {
        var validation = Validate(dto);
        if (validation is not null) return BadRequest(validation);
        var expense = await _db.OperatingExpenses.FindAsync(new object[] { id }, cancellationToken);
        if (expense is null) return NotFound();
        Apply(expense, dto);
        expense.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        await LoadSupplierAsync(expense, cancellationToken);
        return Ok(expense);
    }

    [HttpPut("{id:int}")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxAttachmentFileSizeBytes)]
    public async Task<ActionResult<OperatingExpense>> UpdateWithAttachment(
        int id,
        [FromForm] OperatingExpenseDto dto,
        [FromForm(Name = "attachment")] IFormFile? attachment,
        CancellationToken cancellationToken)
    {
        var validation = Validate(dto);
        if (validation is not null) return BadRequest(validation);
        var expense = await _db.OperatingExpenses.FindAsync(new object[] { id }, cancellationToken);
        if (expense is null) return NotFound();

        var previousStoredAttachmentName = expense.AttachmentStoredFileName;
        string? newStoredAttachmentName = null;
        try
        {
            Apply(expense, dto);
            newStoredAttachmentName = await SaveAttachmentAsync(expense, attachment, cancellationToken);
            expense.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            await DeleteAttachmentAsync(newStoredAttachmentName, cancellationToken);
            return BadRequest(ex.Message);
        }
        catch
        {
            await DeleteAttachmentAsync(newStoredAttachmentName, cancellationToken);
            throw;
        }

        // The replacement is only durable once the row naming it has been saved, so the previous
        // document is removed last: a failure above leaves the expense pointing at a document
        // that still exists.
        if (newStoredAttachmentName is not null)
            await DeleteAttachmentAsync(previousStoredAttachmentName, cancellationToken);
        await LoadSupplierAsync(expense, cancellationToken);
        return Ok(expense);
    }

    // Update fetches by FindAsync rather than an Include'd query, since it may go on to change
    // SupplierId itself; the navigation is loaded once, against the final value, right before the
    // response is serialised.
    private Task LoadSupplierAsync(OperatingExpense expense, CancellationToken cancellationToken) =>
        expense.SupplierId.HasValue
            ? _db.Entry(expense).Reference(e => e.Supplier).LoadAsync(cancellationToken)
            : Task.CompletedTask;

    [HttpGet("{id:int}/attachment")]
    public async Task<IActionResult> GetAttachment(int id, CancellationToken cancellationToken)
    {
        var expense = await _db.OperatingExpenses.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (expense?.AttachmentStoredFileName is null) return NotFound();

        var document = await _documents.OpenReadAsync(
            AttachmentCategory, expense.AttachmentStoredFileName, cancellationToken);
        if (document is null) return NotFound();

        // FileStreamResult disposes the stream once the response has been written. The storage's
        // last-modified instant is carried through so the response keeps the Last-Modified header
        // it had when the attachment was served straight from disk.
        var result = File(
            document.Content,
            expense.AttachmentContentType ?? "application/octet-stream",
            expense.AttachmentFileName);
        result.LastModified = document.LastModified;
        return result;
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        var expense = await _db.OperatingExpenses.FindAsync(new object[] { id }, cancellationToken);
        if (expense is null) return NotFound();
        _db.OperatingExpenses.Remove(expense);
        await _db.SaveChangesAsync(cancellationToken);
        await DeleteAttachmentAsync(expense.AttachmentStoredFileName, cancellationToken);
        return NoContent();
    }

    private static string? Validate(OperatingExpenseDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Description)) return "Description is required.";
        if (dto.AmountExGst < 0 || dto.GstAmount < 0 || dto.TotalAmount < 0) return "Expense amounts cannot be negative.";
        if (dto.ServicePeriodStart.HasValue && dto.ServicePeriodEnd.HasValue && dto.ServicePeriodEnd < dto.ServicePeriodStart)
            return "Service period end must not be before its start.";
        return null;
    }

    private static OperatingExpense FromDto(OperatingExpenseDto dto)
    {
        var expense = new OperatingExpense();
        Apply(expense, dto);
        return expense;
    }

    private static void Apply(OperatingExpense expense, OperatingExpenseDto dto)
    {
        expense.ExpenseDate = dto.ExpenseDate;
        expense.Category = dto.Category;
        expense.Description = dto.Description.Trim();
        expense.AmountExGst = dto.AmountExGst;
        expense.GstAmount = dto.GstAmount;
        expense.TotalAmount = dto.TotalAmount;
        expense.SupplierId = dto.SupplierId;
        expense.SiteId = dto.SiteId;
        expense.MachineId = dto.MachineId;
        expense.ServicePeriodStart = dto.ServicePeriodStart;
        expense.ServicePeriodEnd = dto.ServicePeriodEnd;
        expense.Notes = dto.Notes;
    }

    private async Task<string?> SaveAttachmentAsync(OperatingExpense expense, IFormFile? attachment, CancellationToken cancellationToken)
    {
        if (attachment is null) return null;
        if (attachment.Length == 0 || attachment.Length > MaxAttachmentFileSizeBytes)
            throw new InvalidOperationException("Supporting document must be between 1 byte and 10 MB.");

        var extension = Path.GetExtension(attachment.FileName).ToLowerInvariant();
        if (!AllowedAttachmentExtensions.Contains(extension))
            throw new InvalidOperationException("Supporting document must be an image or PDF.");

        var storedFileName = $"{Guid.NewGuid()}{extension}";
        await using (var source = attachment.OpenReadStream())
        {
            await _documents.SaveAsync(AttachmentCategory, storedFileName, source, cancellationToken);
        }

        expense.AttachmentFileName = Path.GetFileName(attachment.FileName);
        expense.AttachmentStoredFileName = storedFileName;
        expense.AttachmentContentType = AttachmentContentType(extension);
        expense.AttachmentFileSizeBytes = attachment.Length;
        return storedFileName;
    }

    // Supporting documents go through the application document-storage port, so this controller
    // holds no filesystem path. The tenant-owned expense row is always resolved first, and the
    // stored name comes from that row rather than from the request.
    private Task DeleteAttachmentAsync(string? storedFileName, CancellationToken cancellationToken) =>
        storedFileName is null
            ? Task.CompletedTask
            : _documents.DeleteAsync(AttachmentCategory, storedFileName, cancellationToken);

    private static string AttachmentContentType(string extension) => extension switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".pdf" => "application/pdf",
        ".webp" => "image/webp",
        ".heic" => "image/heic",
        _ => "application/octet-stream"
    };
}
