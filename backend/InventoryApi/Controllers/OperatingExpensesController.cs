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
    private readonly AppDbContext _db;

    public OperatingExpensesController(AppDbContext db) => _db = db;

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
                x.SiteId, x.MachineId, x.ReceiptId, x.ServicePeriodStart, x.ServicePeriodEnd, x.Notes))
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

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        var expense = await _db.OperatingExpenses.FindAsync(new object[] { id }, cancellationToken);
        if (expense is null) return NotFound();
        _db.OperatingExpenses.Remove(expense);
        await _db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    private static string? Validate(OperatingExpenseDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Description)) return "Description is required.";
        if (dto.AmountExGst < 0 || dto.GstAmount < 0 || dto.TotalAmount < 0) return "Expense amounts cannot be negative.";
        if (Math.Abs(dto.AmountExGst + dto.GstAmount - dto.TotalAmount) > GstTolerance)
            return "Amount ex GST plus GST must equal total amount within 2 cents.";
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
}
