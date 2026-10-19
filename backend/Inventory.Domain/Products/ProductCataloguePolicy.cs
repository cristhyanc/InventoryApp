namespace Inventory.Domain.Products;

/// <summary>
/// The deterministic invariants on the catalogue fields an operator maintains by hand (issue #736,
/// parent #329 decision P2): a product has a name, and its selling price is not negative.
///
/// <para>Separate from <see cref="ProductRestockPolicy"/> and
/// <see cref="ProductInitialCostPolicy"/>, which own the reorder settings and the initial cost, and
/// applied by both the create and the update use case so a product cannot be edited into a state it
/// could not have been created in.</para>
///
/// <para><c>UnitPrice</c> is the catalogue default selling price and never a cost (AGENTS.md
/// § Inventory and historical costing invariants): this policy bounds it and nothing else, and no
/// calculation reads it.</para>
/// </summary>
public static class ProductCataloguePolicy
{
    public const string NameRequiredMessage = "Product name is required.";
    public const string NegativeUnitPriceMessage = "Unit price cannot be negative.";

    /// <summary>
    /// The category a product carries must be one of the caller's own business's categories. The
    /// message says nothing about why a category is unknown - a category of another business is
    /// invisible, exactly like one that does not exist (AGENTS.md § Tenant ownership and data
    /// isolation).
    /// </summary>
    public const string UnknownCategoryMessage = "The selected category does not exist.";

    /// <summary>Returns the validation error message, or <c>null</c> when the fields are valid.</summary>
    public static string? Validate(string? name, decimal unitPrice)
    {
        if (string.IsNullOrWhiteSpace(name)) return NameRequiredMessage;
        if (unitPrice < 0) return NegativeUnitPriceMessage;
        return null;
    }
}
