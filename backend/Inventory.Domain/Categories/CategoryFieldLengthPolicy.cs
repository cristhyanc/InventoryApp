using Inventory.Domain.FieldLengths;

namespace Inventory.Domain.Categories;

/// <summary>
/// The longest name and description a category may be given (issue #1286, audit #1226 finding
/// DB-03), measured after trimming. A create is judged on what it submits; an update is judged
/// against the stored row, so a category that already holds a longer value can still be saved with
/// the other field changed (<see cref="FieldLengthLimit.ChangeRefusal"/>). Stored values are never
/// shortened. The blank-name rule stays <see cref="CategoryCataloguePolicy.Validate"/>'s.
/// </summary>
public static class CategoryFieldLengthPolicy
{
    /// <summary>The longest category name.</summary>
    public const int MaxNameLength = 200;

    /// <summary>The longest category description.</summary>
    public const int MaxDescriptionLength = 2_000;

    /// <summary>The field names the refusals use.</summary>
    public const string NameLabel = "Category name";

    /// <summary>The field names the refusals use.</summary>
    public const string DescriptionLabel = "Description";

    /// <summary>The first refusal for a new category's text, or <c>null</c> when both fields fit.</summary>
    public static string? ValidateCreate(string? name, string? description) =>
        FieldLengthLimit.Refusal(NameLabel, name, MaxNameLength)
        ?? FieldLengthLimit.Refusal(DescriptionLabel, description, MaxDescriptionLength);

    /// <summary>The first refusal for an update's text judged against the stored category.</summary>
    public static string? ValidateUpdate(
        string? storedName, string? storedDescription, string? name, string? description) =>
        FieldLengthLimit.ChangeRefusal(NameLabel, storedName, name, MaxNameLength)
        ?? FieldLengthLimit.ChangeRefusal(DescriptionLabel, storedDescription, description, MaxDescriptionLength);
}
