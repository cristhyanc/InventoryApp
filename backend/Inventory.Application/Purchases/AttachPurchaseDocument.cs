using Inventory.Application.Attachments;
using Inventory.Application.Documents;
using Inventory.Domain.Attachments;
using Inventory.Domain.Purchases;

namespace Inventory.Application.Purchases;

/// <summary>What an attach amounted to, as the API has to answer it (issue #976).</summary>
public enum AttachPurchaseDocumentOutcome
{
    /// <summary>The document was stored and recorded on the purchase.</summary>
    Attached,

    /// <summary>The uploaded document is empty, too large, or not an image or PDF.</summary>
    InvalidDocument,

    /// <summary>No purchase with that id is visible to the caller's business.</summary>
    PurchaseNotFound,

    /// <summary>That purchase already has a document; replacing one is out of scope.</summary>
    AlreadyHasDocument,

    /// <summary>
    /// The document would take the business past its attachment storage allowance (issue #968).
    /// The purchase is untouched and keeps having no document, which is a state it is allowed to
    /// stay in.
    /// </summary>
    StorageFull,
}

/// <summary>
/// The outcome, the purchase as it now stands (only when it was attached), and the sentence the API
/// reports for a refusal (only when there is one to report - a missing purchase is a bare 404).
/// </summary>
public sealed record AttachPurchaseDocumentResult(
    AttachPurchaseDocumentOutcome Outcome, PurchaseRecord? Purchase, string? Message);

/// <summary>
/// Adds a supporting document to a purchase that has none (issue #976, child of #965).
///
/// <para>It is deliberately <strong>attach only if empty</strong>: a purchase that already has a
/// document is refused rather than having it replaced, because replacing one is out of this
/// issue's scope and silently discarding stored bytes is not recoverable by rolling a transaction
/// back. The emptiness check is not made here - it is made by
/// <see cref="IPurchaseStore.AttachDocumentAsync"/>, inside the same write transaction as the write
/// it permits, so two concurrent attaches cannot both succeed and the loser is told the purchase
/// already has one.</para>
///
/// <para><strong>The document's rules are the create path's rules</strong>, through
/// <see cref="PurchaseDocumentPolicy"/>: the same 10 MiB ceiling and the same allowed types, and a
/// server-generated stored name - the uploaded file name is recorded for display and never used as
/// a storage name.</para>
///
/// <para><strong>A refusal leaves nothing behind.</strong> The bytes are written before the
/// transaction opens, because the only honest emptiness check is the one taken with the write lock
/// held; so whenever the write does not happen - an unknown purchase, a purchase that already has a
/// document, or a persistence failure - this use case deletes the document it had stored. That
/// ordering is the same composition
/// <see cref="UploadPurchase"/> and the operating-expense use cases already use, kept visible at the
/// call site rather than hidden inside storage.</para>
/// </summary>
public sealed class AttachPurchaseDocument
{
    private readonly IPurchaseStore _store;
    private readonly IDocumentStorage _documents;
    private readonly AttachmentQuotaGuard _quota;

    public AttachPurchaseDocument(
        IPurchaseStore store, IDocumentStorage documents, AttachmentQuotaGuard quota)
    {
        _store = store;
        _documents = documents;
        _quota = quota;
    }

    public async Task<AttachPurchaseDocumentResult> Handle(
        int id, PurchaseFileInput file, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);

        if (!PurchaseDocumentPolicy.IsSizeValid(file.Length))
            return Refused(AttachPurchaseDocumentOutcome.InvalidDocument, PurchaseDocumentPolicy.SizeErrorMessage);

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!PurchaseDocumentPolicy.IsExtensionAllowed(extension))
            return Refused(AttachPurchaseDocumentOutcome.InvalidDocument, PurchaseDocumentPolicy.ExtensionErrorMessage);

        // Reserved before the bytes are written (issue #968). The purchase is named so the
        // reservation is computed against what that purchase currently holds - which for an
        // attach-only-if-empty write is nothing, so the whole file is charged.
        var quota = await _quota.ReserveAsync(
            DocumentCategory.PurchaseDocument, id, file.Length, cancellationToken);
        if (quota.Refused)
            return Refused(AttachPurchaseDocumentOutcome.StorageFull, AttachmentStorageQuotaPolicy.StorageFullMessage);

        var storedFileName = $"{Guid.NewGuid()}{extension}";
        await using (var source = file.OpenReadStream())
        {
            await _documents.SaveAsync(DocumentCategory.PurchaseDocument, storedFileName, source, cancellationToken);
        }

        PurchaseDocumentAttachment attachment;
        try
        {
            attachment = await _store.AttachDocumentAsync(
                id,
                new PurchaseFileMetadata(
                    storedFileName, file.ContentType ?? string.Empty, file.FileName, file.Length),
                quota.Reservation,
                cancellationToken);
        }
        catch (AttachmentStorageQuotaExceededException)
        {
            // The reservation had expired and the re-validation inside the write transaction found
            // the upload no longer fits. Nothing was recorded, so the bytes and the reservation go.
            await CleanUpAsync(storedFileName, quota, cancellationToken);
            return Refused(AttachPurchaseDocumentOutcome.StorageFull, AttachmentStorageQuotaPolicy.StorageFullMessage);
        }
        catch
        {
            await CleanUpAsync(storedFileName, quota, cancellationToken);
            throw;
        }

        if (attachment.Outcome == PurchaseDocumentAttachOutcome.Attached)
            return new AttachPurchaseDocumentResult(AttachPurchaseDocumentOutcome.Attached, attachment.Purchase, null);

        // Nothing was recorded, so the bytes nothing refers to go too, and the storage they had
        // reserved stops being held against the business.
        await CleanUpAsync(storedFileName, quota, cancellationToken);

        return attachment.Outcome == PurchaseDocumentAttachOutcome.AlreadyHasDocument
            ? Refused(
                AttachPurchaseDocumentOutcome.AlreadyHasDocument,
                PurchaseDocumentPolicy.AlreadyHasDocumentMessage)
            : Refused(AttachPurchaseDocumentOutcome.PurchaseNotFound, null);
    }

    private async Task CleanUpAsync(
        string storedFileName, AttachmentQuotaDecision quota, CancellationToken cancellationToken)
    {
        await _documents.DeleteAsync(DocumentCategory.PurchaseDocument, storedFileName, cancellationToken);
        await _quota.ReleaseAsync(quota, cancellationToken);
    }

    private static AttachPurchaseDocumentResult Refused(AttachPurchaseDocumentOutcome outcome, string? message) =>
        new(outcome, null, message);
}
