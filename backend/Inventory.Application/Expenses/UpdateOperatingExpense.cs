using Inventory.Application.Attachments;
using Inventory.Application.Documents;
using Inventory.Application.Machines;
using Inventory.Domain.Attachments;
using Inventory.Domain.Expenses;

namespace Inventory.Application.Expenses;

/// <summary>
/// Updates an operating expense, and - when the request carries one - replaces its supporting
/// document.
///
/// <para><strong>This is the one path that replaces an attachment, and since issue #968 it is the
/// one that credits the old file.</strong> The upload is charged only its net increase over the
/// attachment it replaces, so a same-size or smaller file succeeds even at full storage and a
/// larger one needs room for the difference alone. The old file's bytes stop counting in the same
/// transaction that points the row at the new one, and the old physical file is deleted only
/// afterwards - a failed delete leaves bytes for the reconciliation command to report, never a row
/// pointing at a document that is gone.</para>
///
/// <para><strong>Two replacements of one attachment cannot both win.</strong> The reservation
/// records the attachment version it was computed against, and the write commits only while the
/// expense is still at that version. The loser is refused with a conflict, its row change is not
/// made, its reservation is released and its file is removed, so the business is charged for the
/// winning file alone.</para>
/// </summary>
public sealed class UpdateOperatingExpense
{
    private readonly IOperatingExpenseStore _store;
    private readonly IDocumentStorage _documents;
    private readonly ILocalMachineStore _machines;
    private readonly AttachmentQuotaGuard _quota;

    public UpdateOperatingExpense(
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
            var updated = await _store.UpdateAsync(id, fields, null, null, cancellationToken);
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

        // Reserved before any bytes are written (issue #968), against this expense: a replacement
        // is charged only what it adds, and the version the attachment is at right now is recorded
        // so the commit can refuse a replacement that another one got in before.
        var quota = await _quota.ReserveAsync(
            DocumentCategory.ExpenseAttachment, id, attachment.Length, cancellationToken);
        if (quota.Refused)
            return UpdateOperatingExpenseResult.Invalid(AttachmentStorageQuotaPolicy.StorageFullMessage);

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

            var updated = await _store.UpdateAsync(id, fields, metadata, quota.Reservation, cancellationToken);
            if (updated is null)
            {
                await CleanUpAsync(storedFileName, quota, cancellationToken);
                return UpdateOperatingExpenseResult.NotFound();
            }

            // The replacement is only durable once the row naming it has been saved, so the
            // previous document is removed last: a failure above leaves the expense pointing at
            // a document that still exists. Its bytes stopped counting when that row committed,
            // so a failed delete costs the business nothing - it leaves a file the reconciliation
            // command reports as an orphan.
            if (existing.AttachmentStoredFileName is not null)
                await _documents.DeleteAsync(DocumentCategory.ExpenseAttachment, existing.AttachmentStoredFileName, cancellationToken);

            return UpdateOperatingExpenseResult.Success(updated);
        }
        catch (AttachmentReplacementConflictException)
        {
            // Another replacement of this attachment committed first. This one's row change was
            // not made, so its file and its reservation go and the expense keeps the winner's
            // document.
            await CleanUpAsync(storedFileName, quota, cancellationToken);
            return UpdateOperatingExpenseResult.Conflict(AttachmentStorageQuotaPolicy.ReplacementConflictMessage);
        }
        catch (AttachmentStorageQuotaExceededException)
        {
            // The reservation had expired and the write's own re-validation - against the
            // attachment's current size - found the upload no longer fits.
            await CleanUpAsync(storedFileName, quota, cancellationToken);
            return UpdateOperatingExpenseResult.Invalid(AttachmentStorageQuotaPolicy.StorageFullMessage);
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
