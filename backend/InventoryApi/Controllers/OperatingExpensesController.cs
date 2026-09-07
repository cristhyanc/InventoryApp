using InventoryApi.Data;
using InventoryApi.DTOs;
using InventoryApi.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Controllers;

[ApiController]
[Route("api/operating-expenses")]
public sealed class OperatingExpensesController : ControllerBase
{
    private const decimal GstTolerance = 0.02m;
    private const long MaxReceiptFileSizeBytes = 10 * 1024 * 1024;
    private static readonly string[] AllowedReceiptExtensions = { ".jpg", ".jpeg", ".png", ".pdf", ".webp", ".heic" };
    private readonly AppDbContext _db;
    private readonly IWebHostEnvironment _environment;

    public OperatingExpensesController(AppDbContext db, IWebHostEnvironment environment)
    {
        _db = db;
        _environment = environment;
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
                x.SiteId, x.MachineId, x.ReceiptId, x.ReceiptFileName, x.ServicePeriodStart, x.ServicePeriodEnd, x.Notes))
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
    [RequestSizeLimit(MaxReceiptFileSizeBytes)]
    public async Task<ActionResult<OperatingExpense>> CreateWithReceipt(
        [FromForm] OperatingExpenseDto dto,
        [FromForm] IFormFile? receipt,
        CancellationToken cancellationToken)
    {
        var validation = Validate(dto);
        if (validation is not null) return BadRequest(validation);

        var expense = FromDto(dto);
        string? receiptPath = null;
        try
        {
            receiptPath = await SaveReceiptAsync(expense, receipt, cancellationToken);
            _db.OperatingExpenses.Add(expense);
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            DeleteFile(receiptPath);
            return BadRequest(ex.Message);
        }
        catch
        {
            DeleteFile(receiptPath);
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
        return Ok(expense);
    }

    [HttpPut("{id:int}")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxReceiptFileSizeBytes)]
    public async Task<ActionResult<OperatingExpense>> UpdateWithReceipt(
        int id,
        [FromForm] OperatingExpenseDto dto,
        [FromForm] IFormFile? receipt,
        CancellationToken cancellationToken)
    {
        var validation = Validate(dto);
        if (validation is not null) return BadRequest(validation);
        var expense = await _db.OperatingExpenses.FindAsync(new object[] { id }, cancellationToken);
        if (expense is null) return NotFound();

        var previousReceiptPath = ReceiptPath(expense.ReceiptStoredFileName);
        string? newReceiptPath = null;
        try
        {
            Apply(expense, dto);
            newReceiptPath = await SaveReceiptAsync(expense, receipt, cancellationToken);
            expense.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            DeleteFile(newReceiptPath);
            return BadRequest(ex.Message);
        }
        catch
        {
            DeleteFile(newReceiptPath);
            throw;
        }

        if (newReceiptPath is not null) DeleteFile(previousReceiptPath);
        return Ok(expense);
    }

    [HttpGet("{id:int}/receipt")]
    public async Task<IActionResult> GetReceipt(int id, CancellationToken cancellationToken)
    {
        var expense = await _db.OperatingExpenses.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (expense?.ReceiptStoredFileName is null) return NotFound();

        var path = ReceiptPath(expense.ReceiptStoredFileName);
        if (path is null || !System.IO.File.Exists(path)) return NotFound();
        return PhysicalFile(path, expense.ReceiptContentType ?? "application/octet-stream", expense.ReceiptFileName);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        var expense = await _db.OperatingExpenses.FindAsync(new object[] { id }, cancellationToken);
        if (expense is null) return NotFound();
        _db.OperatingExpenses.Remove(expense);
        await _db.SaveChangesAsync(cancellationToken);
        DeleteFile(ReceiptPath(expense.ReceiptStoredFileName));
        return NoContent();
    }

    private static string? Validate(OperatingExpenseDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Description)) return "Description is required.";
        if (dto.AmountExGst < 0 || dto.GstAmount < 0 || dto.TotalAmount < 0) return "Expense amounts cannot be negative.";
        //if (Math.Abs(dto.AmountExGst + dto.GstAmount - dto.TotalAmount) > GstTolerance)
        //    return "Amount ex GST plus GST must equal total amount within 2 cents.";
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
        expense.ReceiptId = dto.ReceiptId;
        expense.ServicePeriodStart = dto.ServicePeriodStart;
        expense.ServicePeriodEnd = dto.ServicePeriodEnd;
        expense.Notes = dto.Notes;
    }

    private async Task<string?> SaveReceiptAsync(OperatingExpense expense, IFormFile? receipt, CancellationToken cancellationToken)
    {
        if (receipt is null) return null;
        if (receipt.Length == 0 || receipt.Length > MaxReceiptFileSizeBytes)
            throw new InvalidOperationException("Receipt file must be between 1 byte and 10 MB.");

        var extension = Path.GetExtension(receipt.FileName).ToLowerInvariant();
        if (!AllowedReceiptExtensions.Contains(extension))
            throw new InvalidOperationException("Receipt must be an image or PDF.");

        var storedFileName = $"{Guid.NewGuid()}{extension}";
        var path = ReceiptPath(storedFileName)!;
        try
        {
            await using var stream = new FileStream(path, FileMode.CreateNew);
            await receipt.CopyToAsync(stream, cancellationToken);
        }
        catch
        {
            DeleteFile(path);
            throw;
        }

        expense.ReceiptFileName = Path.GetFileName(receipt.FileName);
        expense.ReceiptStoredFileName = storedFileName;
        expense.ReceiptContentType = receipt.ContentType;
        return path;
    }

    private string? ReceiptPath(string? storedFileName)
    {
        if (string.IsNullOrWhiteSpace(storedFileName)) return null;
        var webRoot = _environment.WebRootPath ?? Path.Combine(_environment.ContentRootPath, "wwwroot");
        var folder = Path.Combine(webRoot, "expenses");
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, storedFileName);
    }

    private static void DeleteFile(string? path)
    {
        if (path is not null && System.IO.File.Exists(path)) System.IO.File.Delete(path);
    }
}
