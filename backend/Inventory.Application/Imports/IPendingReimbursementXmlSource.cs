namespace Inventory.Application.Imports;

/// <summary>
/// Where pending reimbursement XML files come from, and how one leaves the queue (issue #299).
///
/// The use case never learns a filesystem path, an encoding, or an XML shape: the adapter
/// discovers the pending files, hashes and parses one on request, and removes it once it has
/// been dealt with. Provider failures are translated here rather than crossing this boundary -
/// an unreadable or malformed file is answered as <c>null</c> and a file that cannot be removed
/// as <c>false</c>, both already logged by the adapter - matching the "translated to a plain
/// answer at that layer's own boundary" rule in docs/architecture.md § Exception ownership
/// table. The use case counts either outcome as a failed file, exactly as the former
/// <c>ImportService.ImportPendingXmlFilesAsync</c> catch block did.
/// </summary>
public interface IPendingReimbursementXmlSource
{
    /// <summary>
    /// The file names currently waiting to be imported, in the order the adapter finds them.
    /// An empty result means there is nothing pending.
    /// </summary>
    IReadOnlyList<string> ListPendingFiles();

    /// <summary>
    /// Reads and parses one pending file, or answers <c>null</c> when it cannot be read or does
    /// not contain a usable reimbursement row.
    /// </summary>
    /// <param name="fileName">A name <see cref="ListPendingFiles"/> returned.</param>
    /// <param name="cancellationToken">Caller cancellation; stays cancellation, never a failure.</param>
    Task<PendingReimbursementXmlFile?> ReadAsync(string fileName, CancellationToken cancellationToken);

    /// <summary>
    /// Removes a file from the pending queue once it has been imported or recognised as a
    /// duplicate, and answers whether it is gone. <c>false</c> means the file is still pending
    /// and will be offered again on the next run.
    /// </summary>
    /// <param name="fileName">A name <see cref="ListPendingFiles"/> returned.</param>
    bool TryDiscard(string fileName);
}
