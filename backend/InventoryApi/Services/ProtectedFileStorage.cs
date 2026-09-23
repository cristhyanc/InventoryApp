namespace InventoryApi.Services;

/// <summary>
/// Resolves where uploaded business documents (purchase documents and operating-expense
/// supporting documents) live on disk.
///
/// They are deliberately stored outside the static web root. Static-file middleware does
/// not run controller authorization, so anything under <c>wwwroot</c> is downloadable by
/// anyone who knows the stored file name, regardless of the <c>[Authorize]</c> attributes
/// on the API. Protected documents therefore live under the content root and are only
/// readable through the authenticated API endpoints.
/// </summary>
public static class ProtectedFileStorage
{
    /// <summary>Content-root-relative folder holding every protected uploaded document.</summary>
    public const string RootFolderName = "protected-files";

    /// <summary>
    /// Category folder for purchase documents. The value keeps its legacy "receipts" name so
    /// documents uploaded before the Purchase rename stay reachable.
    /// </summary>
    public const string PurchaseDocumentsCategory = "receipts";

    /// <summary>Category folder for operating-expense supporting documents.</summary>
    public const string ExpenseAttachmentsCategory = "expenses";

    /// <summary>
    /// Path a newly uploaded document must be written to. The stored file name is always
    /// server-generated; <see cref="Path.GetFileName(string)"/> keeps a crafted value from
    /// escaping the category folder.
    /// </summary>
    public static string StoragePath(IWebHostEnvironment environment, string category, string storedFileName)
    {
        var folder = Path.Combine(environment.ContentRootPath, RootFolderName, category);
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, Path.GetFileName(storedFileName));
    }

    /// <summary>
    /// Path of an existing document, or <c>null</c> when it is in neither the protected nor
    /// the legacy location. Documents uploaded before protected storage existed still sit in
    /// <c>wwwroot/{category}</c>; they stay readable and deletable through the API, and no
    /// longer have an anonymous static URL because the API serves no static files.
    /// </summary>
    public static string? ExistingPath(IWebHostEnvironment environment, string category, string? storedFileName)
    {
        if (string.IsNullOrWhiteSpace(storedFileName)) return null;

        var fileName = Path.GetFileName(storedFileName);
        var protectedPath = Path.Combine(environment.ContentRootPath, RootFolderName, category, fileName);
        if (File.Exists(protectedPath)) return protectedPath;

        var webRoot = environment.WebRootPath ?? Path.Combine(environment.ContentRootPath, "wwwroot");
        var legacyPath = Path.Combine(webRoot, category, fileName);
        return File.Exists(legacyPath) ? legacyPath : null;
    }
}
