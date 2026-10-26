namespace Inventory.Domain.Products;

/// <summary>
/// The deterministic invariants on the catalogue fields an operator maintains by hand (issue #736,
/// parent #329 decision P2): a product has a name, and since issue #895 a unit label.
///
/// <para>Separate from <see cref="ProductRestockPolicy"/> and
/// <see cref="ProductInitialCostPolicy"/>, which own the reorder settings and the initial cost, and
/// applied by both the create and the update use case so a product cannot be edited into a state it
/// could not have been created in.</para>
///
/// <para><strong>The unit label is required asymmetrically</strong> (issue #895), and the asymmetry
/// is the rule rather than a concession: a create refuses a blank label outright
/// (<see cref="ValidateUnit"/>), while an update refuses one only when the <em>stored</em> label is
/// nonblank (<see cref="UnitChangeRefusal"/>). So no request can blank a label a product has, and a
/// legacy product whose label was never recorded can still be written - which matters because the
/// Products list deactivates and reactivates a product by sending the whole product back through
/// <c>PUT api/products/{id}</c>, and a blanket rule would strand every such row out of use.
/// Nothing substitutes a label for a legacy product and nothing backfills one: the operator gives
/// it one by editing the product.</para>
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
    /// The one wording for a missing unit label (issue #895), answered by the create's refusal and
    /// by the update's refusal to blank a stored one alike, so the two cannot drift apart and the
    /// Angular form can show the same sentence the API would.
    /// </summary>
    public const string UnitRequiredMessage = "Unit label is required.";

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

    /// <summary>
    /// The unit label a <em>new</em> product must carry (issue #895): the refusal message, or
    /// <c>null</c> when one was submitted. Whitespace is no label - nobody typed it.
    /// </summary>
    public static string? ValidateUnit(string? unit) =>
        string.IsNullOrWhiteSpace(unit) ? UnitRequiredMessage : null;

    /// <summary>
    /// The refusal an <em>update</em> gets for its unit label (issue #895): blanking a label the
    /// product already has, and nothing else. A product whose stored label is already blank may stay
    /// that way, because the activation write sends the whole product back.
    ///
    /// <para>It compares the <paramref name="storedUnit"/> an authoritative read produced, so it is
    /// decided where that row is read - inside the write's own transaction - rather than from a
    /// value the request supplied.</para>
    /// </summary>
    public static string? UnitChangeRefusal(string? storedUnit, string? submittedUnit) =>
        string.IsNullOrWhiteSpace(submittedUnit) && !string.IsNullOrWhiteSpace(storedUnit)
            ? UnitRequiredMessage
            : null;

    /// <summary>
    /// The unit label as it is stored - trimmed, because trailing space is not a different unit - or
    /// <c>null</c> when there is none to store.
    /// </summary>
    public static string? NormalizeUnit(string? unit) =>
        string.IsNullOrWhiteSpace(unit) ? null : unit.Trim();
}
