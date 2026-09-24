namespace Inventory.Infrastructure.Documents;

/// <summary>
/// Which of the filesystem locations a stored document was found in.
///
/// It exists for the document migration, which has to report how many documents are still only
/// in the legacy location - that count is what tells a human whether the <c>wwwroot</c> fallback
/// can eventually be retired. It names a location rather than exposing one: no caller learns a
/// physical path from it.
/// </summary>
public enum DocumentSourceLocation
{
    /// <summary>Under the content root's <c>protected-files/{category}</c> folder.</summary>
    ProtectedStorage = 1,

    /// <summary>
    /// Under the web root's <c>{category}</c> folder, where documents uploaded before protected
    /// storage existed still sit.
    /// </summary>
    LegacyWebRoot = 2,
}
