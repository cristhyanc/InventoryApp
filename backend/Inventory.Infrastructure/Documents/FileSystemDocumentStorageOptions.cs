namespace Inventory.Infrastructure.Documents;

/// <summary>
/// Where <see cref="FileSystemDocumentStorage"/> keeps uploaded business documents.
///
/// The composition root supplies these paths from the host environment, so the adapter never
/// depends on ASP.NET Core's <c>IWebHostEnvironment</c> and nothing inside the Application
/// layer can see a filesystem path at all.
/// </summary>
public sealed class FileSystemDocumentStorageOptions
{
    /// <summary>
    /// The application content root. New documents are written beneath
    /// <c>{ContentRootPath}/protected-files/{category}</c>, outside the static web root, so no
    /// uploaded document has an anonymous URL.
    /// </summary>
    public required string ContentRootPath { get; init; }

    /// <summary>
    /// The static web root, used only to keep reading documents uploaded before protected
    /// storage existed (<c>{WebRootPath}/{category}</c>). When it is not configured the
    /// conventional <c>{ContentRootPath}/wwwroot</c> is assumed, matching the host default.
    /// </summary>
    public string? WebRootPath { get; init; }
}
