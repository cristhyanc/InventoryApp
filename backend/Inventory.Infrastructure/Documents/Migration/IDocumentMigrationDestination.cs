using Inventory.Application.Documents;
using Inventory.Domain.Tenancy;

namespace Inventory.Infrastructure.Documents.Migration;

/// <summary>
/// The destination side of the human-invoked document migration (issue #39, checkpoint 3).
///
/// This is deliberately <em>not</em> <see cref="IDocumentStorage"/>. That port serves
/// authenticated requests and derives the business from the trusted per-request
/// <c>IBusinessScope</c>; it has no business parameter, and
/// <see cref="AzureBlobDocumentStorage"/> refuses the unscoped scope outright. Widening it so a
/// maintenance job could pass a business would put a business-id parameter on the one path every
/// upload and download in the application already uses - and a parameter that exists is a
/// parameter that can be supplied from the wrong place.
///
/// So cross-business access gets its own interface instead, used from exactly one place: the
/// <c>migrate-documents</c> command, after it has loaded a record from the database. The
/// <see cref="BusinessId"/> it takes is explicit precisely because its provenance matters - it is
/// read from the persisted tenant-owned record that owns the document, never from a folder name,
/// a file name, a blob name or the command line.
/// </summary>
public interface IDocumentMigrationDestination
{
    /// <summary>
    /// The key a document would occupy, for reporting. It is derived the same way the running
    /// application derives it, so an operator reading the report is looking at the name the API
    /// will later ask for.
    /// </summary>
    /// <exception cref="ArgumentException">The stored name does not reduce to a usable file name.</exception>
    string KeyFor(BusinessId businessId, DocumentCategory category, string storedFileName);

    /// <summary>
    /// What is stored at that key today, or <c>null</c> when nothing is. The fingerprint is
    /// computed from the destination's own bytes rather than from its reported metadata, because
    /// the question this answers is "are these the same document", and only the bytes settle it.
    ///
    /// A missing document is an ordinary answer. A missing container, a refused authorization or
    /// an unreachable account is not, and must surface as the failure it is.
    /// </summary>
    Task<DocumentFingerprint?> InspectAsync(
        BusinessId businessId,
        DocumentCategory category,
        string storedFileName,
        CancellationToken cancellationToken);

    /// <summary>
    /// Writes the document only if that key is free, reporting <c>false</c> when it is not.
    /// Nothing is ever overwritten: an occupied key means a human has to look, because the one
    /// thing a migration must not do is replace a document nobody asked it to touch.
    /// </summary>
    Task<bool> UploadIfAbsentAsync(
        BusinessId businessId,
        DocumentCategory category,
        string storedFileName,
        Stream content,
        CancellationToken cancellationToken);
}
