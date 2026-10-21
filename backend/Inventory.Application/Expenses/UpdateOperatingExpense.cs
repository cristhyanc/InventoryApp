using Inventory.Application.Documents;
using Inventory.Application.Machines;
using Inventory.Domain.Expenses;

namespace Inventory.Application.Expenses;

public sealed class UpdateOperatingExpense
{
    private readonly IOperatingExpenseStore _store;
    private readonly IDocumentStorage _documents;
    private readonly ILocalMachineStore _machines;

    public UpdateOperatingExpense(
        IOperatingExpenseStore store, IDocumentStorage documents, ILocalMachineStore machines)
    {
        _store = store;
        _documents = documents;
        _machines = machines;
    }

    public async Task<UpdateOperatingExpenseResult> Handle(
        int id, OperatingExpenseFields fields, ExpenseAttachmentInput? attachment, CancellationToken cancellationToken)
    {
        if (!OperatingExpenseDetails.TryCreate(
                fields.Description, fields.AmountExGst, fields.GstAmount, fields.TotalAmount,
                fields.ServicePeriodStart, fields.ServicePeriodEnd, out var details, out var validationError))
            return UpdateOperatingExpenseResult.Invalid(validationError!);

        // The submitted machine is this business's own machine row (issue #780). Checked with the
        // other field rules, before the expense is read or an attachment is written, so a rejected
        // machine leaves the stored expense and its document exactly as they were.
        if (!await OperatingExpenseMachine.IsThisBusinessesAsync(_machines, fields.MachineId, cancellationToken))
            return UpdateOperatingExpenseResult.Invalid(OperatingExpenseDetails.UnknownMachineMessage);

        fields = fields with { Description = details!.Description };

        if (attachment is null)
        {
            var updated = await _store.UpdateAsync(id, fields, null, cancellationToken);
            return updated is null ? UpdateOperatingExpenseResult.NotFound() : UpdateOperatingExpenseResult.Success(updated);
        }

        // Existence is resolved before the attachment is validated, exactly as the retired
        // controller did: an unknown id answers not-found even when the request also carries an
        // oversized or disallowed attachment.
        var existing = await _store.FindByIdAsync(id, cancellationToken);
        if (existing is null) return UpdateOperatingExpenseResult.NotFound();

        var extension = Path.GetExtension(attachment.FileName).ToLowerInvariant();
        if (!ExpenseAttachmentPolicy.IsSizeValid(attachment.Length))
            return UpdateOperatingExpenseResult.Invalid(ExpenseAttachmentPolicy.SizeErrorMessage);
        if (!ExpenseAttachmentPolicy.IsExtensionAllowed(extension))
            return UpdateOperatingExpenseResult.Invalid(ExpenseAttachmentPolicy.ExtensionErrorMessage);

        var storedFileName = $"{Guid.NewGuid()}{extension}";
        try
        {
            await using (var source = attachment.OpenReadStream())
            {
                await _documents.SaveAsync(DocumentCategory.ExpenseAttachment, storedFileName, source, cancellationToken);
            }

            var metadata = new OperatingExpenseAttachmentMetadata(
                Path.GetFileName(attachment.FileName), storedFileName,
                ExpenseAttachmentPolicy.ContentTypeFor(extension), attachment.Length);

            var updated = await _store.UpdateAsync(id, fields, metadata, cancellationToken);
            if (updated is null) return UpdateOperatingExpenseResult.NotFound();

            // The replacement is only durable once the row naming it has been saved, so the
            // previous document is removed last: a failure above leaves the expense pointing at
            // a document that still exists.
            if (existing.AttachmentStoredFileName is not null)
                await _documents.DeleteAsync(DocumentCategory.ExpenseAttachment, existing.AttachmentStoredFileName, cancellationToken);

            return UpdateOperatingExpenseResult.Success(updated);
        }
        catch
        {
            await _documents.DeleteAsync(DocumentCategory.ExpenseAttachment, storedFileName, cancellationToken);
            throw;
        }
    }
}
