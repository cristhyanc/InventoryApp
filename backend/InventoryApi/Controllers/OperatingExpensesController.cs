using Inventory.Application.Expenses;
using Inventory.Domain.Expenses;
using InventoryApi.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

[ApiController]
[Route("api/operating-expenses")]
[Authorize]
[RequiredScope("access_as_user")]
public sealed class OperatingExpensesController : ControllerBase
{
    private const long MaxAttachmentFileSizeBytes = ExpenseAttachmentPolicy.MaxFileSizeBytes;

    private readonly ListOperatingExpenses _listExpenses;
    private readonly GetOperatingExpense _getExpense;
    private readonly GetOperatingExpenseAttachment _getAttachment;
    private readonly CreateOperatingExpense _createExpense;
    private readonly UpdateOperatingExpense _updateExpense;
    private readonly DeleteOperatingExpense _deleteExpense;

    public OperatingExpensesController(
        ListOperatingExpenses listExpenses,
        GetOperatingExpense getExpense,
        GetOperatingExpenseAttachment getAttachment,
        CreateOperatingExpense createExpense,
        UpdateOperatingExpense updateExpense,
        DeleteOperatingExpense deleteExpense)
    {
        _listExpenses = listExpenses;
        _getExpense = getExpense;
        _getAttachment = getAttachment;
        _createExpense = createExpense;
        _updateExpense = updateExpense;
        _deleteExpense = deleteExpense;
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
        var filter = new OperatingExpenseFilter(from, to, (ExpenseCategory?)category, supplierId, siteId, machineId);
        var rows = await _listExpenses.Handle(filter, cancellationToken);
        return Ok(rows.Select(ToReportRowDto).ToList());
    }

    [HttpPost]
    public async Task<ActionResult<OperatingExpenseResponse>> Create(OperatingExpenseDto dto, CancellationToken cancellationToken)
    {
        var result = await _createExpense.Handle(ToFields(dto), null, cancellationToken);
        if (!result.IsValid) return BadRequest(result.ValidationError);
        return CreatedAtAction(nameof(GetById), new { id = result.Record!.Id }, ToResponse(result.Record));
    }

    [HttpPost]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxAttachmentFileSizeBytes)]
    public async Task<ActionResult<OperatingExpenseResponse>> CreateWithAttachment(
        [FromForm] OperatingExpenseDto dto,
        [FromForm(Name = "attachment")] IFormFile? attachment,
        CancellationToken cancellationToken)
    {
        var result = await _createExpense.Handle(ToFields(dto), ToAttachmentInput(attachment), cancellationToken);
        if (!result.IsValid) return BadRequest(result.ValidationError);
        return CreatedAtAction(nameof(GetById), new { id = result.Record!.Id }, ToResponse(result.Record));
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<OperatingExpenseResponse>> GetById(int id, CancellationToken cancellationToken)
    {
        var expense = await _getExpense.Handle(id, cancellationToken);
        return expense is null ? NotFound() : Ok(ToResponse(expense));
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<OperatingExpenseResponse>> Update(int id, OperatingExpenseDto dto, CancellationToken cancellationToken)
    {
        var result = await _updateExpense.Handle(id, ToFields(dto), null, cancellationToken);
        return ToActionResult(result);
    }

    [HttpPut("{id:int}")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxAttachmentFileSizeBytes)]
    public async Task<ActionResult<OperatingExpenseResponse>> UpdateWithAttachment(
        int id,
        [FromForm] OperatingExpenseDto dto,
        [FromForm(Name = "attachment")] IFormFile? attachment,
        CancellationToken cancellationToken)
    {
        var result = await _updateExpense.Handle(id, ToFields(dto), ToAttachmentInput(attachment), cancellationToken);
        return ToActionResult(result);
    }

    [HttpGet("{id:int}/attachment")]
    public async Task<IActionResult> GetAttachment(int id, CancellationToken cancellationToken)
    {
        var attachment = await _getAttachment.Handle(id, cancellationToken);
        if (attachment is null) return NotFound();

        // FileStreamResult disposes the stream once the response has been written. The storage's
        // last-modified instant is carried through so the response keeps the Last-Modified header
        // it had when the attachment was served straight from disk.
        var result = File(
            attachment.Document.Content,
            attachment.ContentType ?? "application/octet-stream",
            attachment.FileName);
        result.LastModified = attachment.Document.LastModified;
        return result;
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        var deleted = await _deleteExpense.Handle(id, cancellationToken);
        return deleted ? NoContent() : NotFound();
    }

    private ActionResult<OperatingExpenseResponse> ToActionResult(UpdateOperatingExpenseResult result)
    {
        if (result.IsNotFound) return NotFound();
        if (!result.IsValid) return BadRequest(result.ValidationError);
        return Ok(ToResponse(result.Record!));
    }

    private static OperatingExpenseFields ToFields(OperatingExpenseDto dto) => new(
        dto.ExpenseDate, (ExpenseCategory)dto.Category, dto.Description, dto.AmountExGst, dto.GstAmount,
        dto.TotalAmount, dto.SupplierId, dto.SiteId, dto.MachineId, dto.ServicePeriodStart, dto.ServicePeriodEnd,
        dto.Notes);

    private static ExpenseAttachmentInput? ToAttachmentInput(IFormFile? attachment) =>
        attachment is null ? null : new ExpenseAttachmentInput(attachment.FileName, attachment.Length, attachment.OpenReadStream);

    private static OperatingExpenseReportRowDto ToReportRowDto(OperatingExpenseListItem item) => new(
        item.Id, item.ExpenseDate, (OperatingExpenseCategory)item.Category, item.Description, item.AmountExGst,
        item.GstAmount, item.TotalAmount, item.SupplierId, item.SupplierName, item.SiteId, item.MachineId,
        item.AttachmentFileName, item.AttachmentFileSizeBytes, item.ServicePeriodStart, item.ServicePeriodEnd,
        item.Notes);

    private static OperatingExpenseResponse ToResponse(OperatingExpenseRecord record) => new(
        record.Id, record.ExpenseDate, (OperatingExpenseCategory)record.Category, record.Description,
        record.AmountExGst, record.GstAmount, record.TotalAmount, record.SupplierId,
        record.Supplier is null
            ? null
            : new SupplierResponse(
                record.Supplier.Id, record.Supplier.Name, record.Supplier.ContactName, record.Supplier.Phone,
                record.Supplier.Email, record.Supplier.Address),
        record.SiteId, record.MachineId, record.AttachmentFileName, record.AttachmentContentType,
        record.AttachmentFileSizeBytes, record.ServicePeriodStart, record.ServicePeriodEnd, record.Notes,
        record.CreatedAt, record.UpdatedAt);
}
