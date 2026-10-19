using System.Globalization;

namespace Inventory.Domain.Categories;

/// <summary>
/// The deterministic invariants on a category an operator maintains by hand (issue #736, parent #329
/// decision P2): it has a name, that name is unique inside the business, and a category products
/// still use is not deleted.
///
/// <para><strong>Uniqueness is per business</strong>, like every other constraint over a value a
/// person or another system supplies (AGENTS.md § Tenant ownership and data isolation): two
/// businesses may legitimately both have a "Drinks" category. It is compared without case, because
/// "Drinks" and "drinks" are the same category to the operator typing the second one.</para>
///
/// <para>Deterministic: the answers are functions of their arguments, so the use case, the adapter
/// that looks for a clash and a test all decide identically.</para>
/// </summary>
public static class CategoryCataloguePolicy
{
    public const string NameRequiredMessage = "Category name is required.";

    /// <summary>Returns the validation error message, or <c>null</c> when the name is usable.</summary>
    public static string? Validate(string? name) =>
        string.IsNullOrWhiteSpace(name) ? NameRequiredMessage : null;

    /// <summary>
    /// The name as it is stored and compared: trimmed, because trailing space is not a second
    /// category.
    /// </summary>
    public static string Normalize(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.Trim();
    }

    /// <summary>Why a name was refused, for a business that already has a category called that.</summary>
    public static string DuplicateNameMessage(string name) =>
        $"This business already has a category called \"{name}\". Category names must be unique "
            + "within a business. Nothing was saved.";

    /// <summary>
    /// Why a category may not be deleted, or <c>null</c> when it may be.
    ///
    /// <para>The count is in the message because it is what the operator has to act on: a product's
    /// category is set to nothing when its category disappears, so deleting a category products
    /// still use would quietly uncategorise them.</para>
    /// </summary>
    public static string? DeletionRefusal(int productCount) =>
        productCount <= 0
            ? null
            : $"This category cannot be deleted because {Products(productCount)} it. Move those "
                + "products to another category first. Nothing was deleted.";

    private static string Products(int productCount) =>
        productCount == 1
            ? "1 product uses"
            : $"{productCount.ToString(CultureInfo.InvariantCulture)} products use";
}
