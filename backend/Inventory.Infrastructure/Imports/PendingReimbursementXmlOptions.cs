namespace Inventory.Infrastructure.Imports;

/// <summary>
/// Where <see cref="FileSystemPendingReimbursementXmlSource"/> looks for pending reimbursement
/// XML files (issue #299).
///
/// The composition root supplies these paths from the host environment, so the adapter never
/// depends on ASP.NET Core's <c>IWebHostEnvironment</c> - the same arrangement
/// <see cref="Documents.FileSystemDocumentStorageOptions"/> already uses.
/// </summary>
public sealed class PendingReimbursementXmlOptions
{
    /// <summary>
    /// The application content root, used only to derive the conventional
    /// <c>{ContentRootPath}/wwwroot</c> web root when the host does not report one.
    /// </summary>
    public required string ContentRootPath { get; init; }

    /// <summary>
    /// The static web root. Pending files are read from <c>{WebRootPath}/ImportedFiles</c>,
    /// which is where the import has always collected them.
    /// </summary>
    public string? WebRootPath { get; init; }
}
