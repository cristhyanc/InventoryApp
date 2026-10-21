namespace Inventory.Domain.Products;

/// <summary>
/// The deterministic invariants on the catalogue fields an operator maintains by hand (issue #736,
/// parent #329 decision P2): a product has a name.
///
/// <para>Separate from <see cref="ProductRestockPolicy"/> and
/// <see cref="ProductInitialCostPolicy"/>, which own the reorder settings and the initial cost, and
/// applied by both the create and the update use case so a product cannot be edited into a state it
/// could not have been created in.</para>
///
/// <para>The catalogue default selling price it also bounded, <c>UnitPrice</c>, is gone with the
/// field (issue #792, A2 of #786): a product carries no selling price, so there is no longer a
/// price to validate here. The costs a product does carry are bounded by
/// <see cref="ProductInitialCostPolicy"/> and by the purchase rules, not by this one.</para>
/// </summary>
public static class ProductCataloguePolicy
{
    public const string NameRequiredMessage = "Product name is required.";

    /// <summary>
    /// The category a product carries must be one of the caller's own business's categories. The
    /// message says nothing about why a category is unknown - a category of another business is
    /// invisible, exactly like one that does not exist (AGENTS.md § Tenant ownership and data
    /// isolation).
    /// </summary>
    public const string UnknownCategoryMessage = "The selected category does not exist.";

    /// <summary>Returns the validation error message, or <c>null</c> when the fields are valid.</summary>
    public static string? Validate(string? name) =>
        string.IsNullOrWhiteSpace(name) ? NameRequiredMessage : null;
}
