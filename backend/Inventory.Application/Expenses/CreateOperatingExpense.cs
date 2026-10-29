using Inventory.Application.Attachments;
using Inventory.Application.Documents;
using Inventory.Application.Machines;
using Inventory.Domain.Attachments;
using Inventory.Domain.Expenses;

namespace Inventory.Application.Expenses;

/// <summary>
/// Records an operating expense, with or without a supporting document.
///
/// <para><strong>Since issue #968 the attachment's storage is reserved before it is written.</strong>
/// The reservation is taken after the field and machine rules and before the bytes reach storage,
/// so an upload that does not fit the business's plan allowance is refused without writing
/// anything; it is consumed inside the insert's own transaction, so the reserved bytes become the
/// attachment's recorded size with the row. Only the <em>attachment</em> is refused for storage -
/// the expense itself can always be recorded without one - and while no plan is enforced nothing
/// is reserved at all.</para>
/// </summary>
public sealed class CreateOperatingExpense
{
    private readonly IOperatingExpenseStore _store;
    private readonly IDocumentStorage _documents;
    private readonly ILocalMachineStore _machines;
    private readonly AttachmentQuotaGuard _quota;

    public CreateOperatingExpense(
        IOperatingExpenseStore store,
        IDocumentStorage documents,
        ILocalMachineStore machines,
        AttachmentQuotaGuard quota)
    {
        _store = store;
        _documents = documents;
        _machines = machines;
        _quota = quota;
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
            var record = await _store.AddAsync(fields, null, null, cancellationToken);
            return CreateOperatingExpenseResult.Success(record);
        }

        var extension = Path.GetExtension(attachment.FileName).ToLowerInvariant();
        if (!ExpenseAttachmentPolicy.IsSizeValid(attachment.Length))
            return CreateOperatingExpenseResult.Invalid(ExpenseAttachmentPolicy.SizeErrorMessage);
        if (!ExpenseAttachmentPolicy.IsExtensionAllowed(extension))
            return CreateOperatingExpenseResult.Invalid(ExpenseAttachmentPolicy.ExtensionErrorMessage);

        // Reserved before any bytes are written (issue #968). The expense does not exist yet, so
        // the upload is charged its whole size.
        var quota = await _quota.ReserveAsync(
            DocumentCategory.ExpenseAttachment, null, attachment.Length, cancellationToken);
        if (quota.Refused)
            return CreateOperatingExpenseResult.Invalid(AttachmentStorageQuotaPolicy.StorageFullMessage);

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

            var record = await _store.AddAsync(fields, metadata, quota.Reservation, cancellationToken);
            return CreateOperatingExpenseResult.Success(record);
        }
        catch (AttachmentStorageQuotaExceededException)
        {
            // The reservation had expired and the insert's own re-validation found the upload no
            // longer fits. Nothing was recorded, so the bytes and the reservation go with it.
            await CleanUpAsync(storedFileName, quota, cancellationToken);
            return CreateOperatingExpenseResult.Invalid(AttachmentStorageQuotaPolicy.StorageFullMessage);
        }
        catch
        {
            await CleanUpAsync(storedFileName, quota, cancellationToken);
            throw;
        }
    }

    private async Task CleanUpAsync(
        string storedFileName, AttachmentQuotaDecision quota, CancellationToken cancellationToken)
    {
        await _documents.DeleteAsync(DocumentCategory.ExpenseAttachment, storedFileName, cancellationToken);
        await _quota.ReleaseAsync(quota, cancellationToken);
    }
}
