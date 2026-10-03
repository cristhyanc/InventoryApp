namespace Inventory.Application.Imports;

/// <summary>
/// The narrow persistence port the pending-XML import needs (issue #299): the file-hash
/// idempotency question, and one atomic write of a file with its reimbursement graph.
/// </summary>
public interface IImportedReimbursementStore
{
    /// <summary>
    /// Whether this business has already imported a file with these exact bytes. The lookup is
    /// tenant-scoped by the central ownership mechanism, never by a filter of its own, so the
    /// same file imported by another business is not reported as a duplicate here.
    /// </summary>
    Task<bool> HasFileWithContentHashAsync(string contentHash, CancellationToken cancellationToken);

    /// <summary>
    /// Persists the imported file and every reimbursement, device, device payment, fee and
    /// payment method it carries, as one unit of work. The caller has already established that
    /// <see cref="HasFileWithContentHashAsync"/> answered <c>false</c>.
    /// </summary>
    /// <param name="file">The parsed file; its reimbursement list is never empty.</param>
    /// <param name="importedAtUtc">When the import happened, as a UTC instant.</param>
    /// <param name="cancellationToken">Caller cancellation.</param>
    Task ImportAsync(PendingReimbursementXmlFile file, DateTime importedAtUtc, CancellationToken cancellationToken);
}
