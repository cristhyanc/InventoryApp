using Inventory.Application.Documents;
using Inventory.Application.Machines;
using Inventory.Domain.Expenses;

namespace Inventory.Application.Expenses;

public sealed class CreateOperatingExpense
{
    private readonly IOperatingExpenseStore _store;
    private readonly IDocumentStorage _documents;
    private readonly ILocalMachineStore _machines;

    public CreateOperatingExpense(
        IOperatingExpenseStore store, IDocumentStorage documents, ILocalMachineStore machines)
    {
        _store = store;
        _documents = documents;
        _machines = machines;
    }

    public async Task<CreateOperatingExpenseResult> Handle(
        OperatingExpenseFields fields, ExpenseAttachmentInput? attachment, CancellationToken cancellationToken)
    {
        if (!OperatingExpenseDetails.TryCreate(
                fields.Description, fields.AmountExGst, fields.GstAmount, fields.TotalAmount,
                fields.ServicePeriodStart, fields.ServicePeriodEnd, out var details, out var validationError))
            return CreateOperatingExpenseResult.Invalid(validationError!);

        // The machine is this business's own machine row (issue #780), so it is checked against the
        // business's machines before anything is stored or any attachment is written. The machine
        // store reads through the ordinary tenant filters, so another business's machine is simply
        // not there; the foreign key refuses the same thing at the write, but a caller deserves the
        // reason rather than a constraint violation.
        if (!await OperatingExpenseMachine.IsThisBusinessesAsync(_machines, fields.MachineId, cancellationToken))
            return CreateOperatingExpenseResult.Invalid(OperatingExpenseDetails.UnknownMachineMessage);

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
