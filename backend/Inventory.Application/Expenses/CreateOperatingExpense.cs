using Inventory.Application.Documents;
using Inventory.Domain.Expenses;

namespace Inventory.Application.Expenses;

public sealed class CreateOperatingExpense
{
    private readonly IOperatingExpenseStore _store;
    private readonly IDocumentStorage _documents;

    public CreateOperatingExpense(IOperatingExpenseStore store, IDocumentStorage documents)
    {
        _store = store;
        _documents = documents;
    }

    public async Task<CreateOperatingExpenseResult> Handle(
        OperatingExpenseFields fields, ExpenseAttachmentInput? attachment, CancellationToken cancellationToken)
    {
        if (!OperatingExpenseDetails.TryCreate(
                fields.Description, fields.AmountExGst, fields.GstAmount, fields.TotalAmount,
                fields.ServicePeriodStart, fields.ServicePeriodEnd, out var details, out var validationError))
            return CreateOperatingExpenseResult.Invalid(validationError!);

        fields = fields with { Description = details!.Description };

        if (attachment is null)
        {
            var record = await _store.AddAsync(fields, null, cancellationToken);
            return CreateOperatingExpenseResult.Success(record);
        }

        var extension = Path.GetExtension(attachment.FileName).ToLowerInvariant();
        if (!ExpenseAttachmentPolicy.IsSizeValid(attachment.Length))
            return CreateOperatingExpenseResult.Invalid(ExpenseAttachmentPolicy.SizeErrorMessage);
        if (!ExpenseAttachmentPolicy.IsExtensionAllowed(extension))
            return CreateOperatingExpenseResult.Invalid(ExpenseAttachmentPolicy.ExtensionErrorMessage);

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

            var record = await _store.AddAsync(fields, metadata, cancellationToken);
            return CreateOperatingExpenseResult.Success(record);
        }
        catch
        {
            await _documents.DeleteAsync(DocumentCategory.ExpenseAttachment, storedFileName, cancellationToken);
            throw;
        }
    }
}
