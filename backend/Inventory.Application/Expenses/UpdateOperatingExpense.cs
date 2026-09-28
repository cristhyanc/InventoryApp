using Inventory.Application.Documents;
using Inventory.Domain.Expenses;

namespace Inventory.Application.Expenses;

public sealed class UpdateOperatingExpense
{
    private readonly IOperatingExpenseStore _store;
    private readonly IDocumentStorage _documents;

    public UpdateOperatingExpense(IOperatingExpenseStore store, IDocumentStorage documents)
    {
        _store = store;
        _documents = documents;
    }

    public async Task<UpdateOperatingExpenseResult> Handle(
        int id, OperatingExpenseFields fields, ExpenseAttachmentInput? attachment, CancellationToken cancellationToken)
    {
        if (!OperatingExpenseDetails.TryCreate(
                fields.Description, fields.AmountExGst, fields.GstAmount, fields.TotalAmount,
                fields.ServicePeriodStart, fields.ServicePeriodEnd, out var details, out var validationError))
            return UpdateOperatingExpenseResult.Invalid(validationError!);

        fields = fields with { Description = details!.Description };

        if (attachment is null)
        {
            var updated = await _store.UpdateAsync(id, fields, null, cancellationToken);
            return updated is null ? UpdateOperatingExpenseResult.NotFound() : UpdateOperatingExpenseResult.Success(updated);
        }

        var extension = Path.GetExtension(attachment.FileName).ToLowerInvariant();
        if (!ExpenseAttachmentPolicy.IsSizeValid(attachment.Length))
            return UpdateOperatingExpenseResult.Invalid(ExpenseAttachmentPolicy.SizeErrorMessage);
        if (!ExpenseAttachmentPolicy.IsExtensionAllowed(extension))
            return UpdateOperatingExpenseResult.Invalid(ExpenseAttachmentPolicy.ExtensionErrorMessage);

        var existing = await _store.FindByIdAsync(id, cancellationToken);
        if (existing is null) return UpdateOperatingExpenseResult.NotFound();

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
