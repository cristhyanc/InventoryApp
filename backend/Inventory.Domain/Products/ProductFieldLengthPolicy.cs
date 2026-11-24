using Inventory.Domain.FieldLengths;

namespace Inventory.Domain.Products;

/// <summary>
/// The longest name, SKU, unit label and description a product a person maintains may be given
/// (issue #1286, audit #1226 finding DB-03), measured after trimming.
///
/// <para>The database enforces none of these (SQLite ignores a declared column length and these
/// columns declare none), so this is the only limit. A create is judged on what it submits; an
/// update is judged against the stored row, so a product that already holds a longer value can
/// still be saved with a different field changed - only a new over-long value is refused
/// (<see cref="FieldLengthLimit.ChangeRefusal"/>). Stored values are never shortened.</para>
/// </summary>
public static class ProductFieldLengthPolicy
{
    /// <summary>The longest product name.</summary>
    public const int MaxNameLength = 200;

    /// <summary>The longest SKU.</summary>
    public const int MaxSkuLength = 100;

    /// <summary>The longest unit label.</summary>
    public const int MaxUnitLength = 100;

    /// <summary>The longest description.</summary>
    public const int MaxDescriptionLength = 2_000;

    /// <summary>The field names the refusals use.</summary>
    public const string NameLabel = "Product name";

    /// <summary>The field names the refusals use.</summary>
    public const string SkuLabel = "SKU";

    /// <summary>The field names the refusals use.</summary>
    public const string UnitLabel = "Unit label";

    /// <summary>The field names the refusals use.</summary>
    public const string DescriptionLabel = "Description";

    /// <summary>The first refusal for a new product's text, or <c>null</c> when every field fits.</summary>
    public static string? ValidateCreate(ProductText submitted) =>
        FieldLengthLimit.Refusal(NameLabel, submitted.Name, MaxNameLength)
        ?? FieldLengthLimit.Refusal(SkuLabel, submitted.Sku, MaxSkuLength)
        ?? FieldLengthLimit.Refusal(UnitLabel, submitted.Unit, MaxUnitLength)
        ?? FieldLengthLimit.Refusal(DescriptionLabel, submitted.Description, MaxDescriptionLength);

    /// <summary>
    /// The first refusal for an update's text judged against the stored product, or <c>null</c>
    /// when every field either fits or is left as it is stored.
    /// </summary>
    public static string? ValidateUpdate(ProductText stored, ProductText submitted) =>
        FieldLengthLimit.ChangeRefusal(NameLabel, stored.Name, submitted.Name, MaxNameLength)
        ?? FieldLengthLimit.ChangeRefusal(SkuLabel, stored.Sku, submitted.Sku, MaxSkuLength)
        ?? FieldLengthLimit.ChangeRefusal(UnitLabel, stored.Unit, submitted.Unit, MaxUnitLength)
        ?? FieldLengthLimit.ChangeRefusal(
            DescriptionLabel, stored.Description, submitted.Description, MaxDescriptionLength);
}

/// <summary>A product's free-text fields, as stored or as submitted (issue #1286).</summary>
public readonly record struct ProductText(string? Name, string? Sku, string? Unit, string? Description);
