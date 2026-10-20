namespace Inventory.Domain.Providers;

/// <summary>
/// Which catalogue fields the business's sales provider owns on a row it names, and the refusal a
/// local edit of one gets (issue #736, parent #329 decisions P2 and P3).
///
/// <para><strong>A row the provider names is partly the provider's.</strong> A product carrying an
/// <c>ExternalProductId</c>, or a category carrying an <c>ExternalCategoryId</c>, takes its name -
/// and, for a product, its category - from the provider's catalogue, because the catalogue import
/// applies exactly those fields on every run without asking (docs/architecture.md § Provider
/// catalogue review). Accepting a local edit of one would present a value the next import silently
/// discards.
/// Everything else on the row is local: the price, the SKU, the unit, the supplier, the restock
/// settings, the active flag and the stock itself stay editable, and nothing here touches them.</para>
///
/// <para><strong>The field names are the wire names</strong> the product and category responses
/// publish, so a client can lock exactly the inputs the server will refuse without a second mapping
/// of its own.</para>
///
/// <para>It holds no clock, no database and no configuration: every answer is a function of its
/// arguments, so the use case that refuses an edit, the response that declares the lock and a test
/// all decide identically.</para>
/// </summary>
public static class ProviderManagedCatalogueFields
{
    /// <summary>The wire name of a product's or a category's name field.</summary>
    public const string Name = "name";

    /// <summary>The wire name of a product's category field.</summary>
    public const string CategoryId = "categoryId";

    private static readonly string[] ProductFields = [Name, CategoryId];
    private static readonly string[] CategoryFields = [Name];
    private static readonly string[] NoFields = [];

    /// <summary>
    /// The fields a provider manages on a product: its name and its category while the provider
    /// names the product, and nothing at all otherwise. An empty list is the answer for a product
    /// nobody but this business maintains - a real answer, not a missing one.
    /// </summary>
    public static IReadOnlyList<string> ForProduct(bool isProviderManaged) =>
        isProviderManaged ? ProductFields : NoFields;

    /// <summary>The fields a provider manages on a category: its name, on the same terms.</summary>
    public static IReadOnlyList<string> ForCategory(bool isProviderManaged) =>
        isProviderManaged ? CategoryFields : NoFields;

    /// <summary>
    /// Why a product's name may not be changed locally. It names the provider, because the operator
    /// has to know where the value comes from to change it in the right place, and says what is
    /// still editable so the refusal does not read as "this product is read-only".
    /// </summary>
    public static string ProductNameRefusal(ExternalProvider? provider) =>
        $"This product's name is managed by {ProviderName(provider)} and cannot be changed here. "
            + "Its price and its other local fields can still be edited. Nothing was saved.";

    /// <summary>Why a product's category may not be changed locally.</summary>
    public static string ProductCategoryRefusal(ExternalProvider? provider) =>
        $"This product's category is managed by {ProviderName(provider)} and cannot be changed here. "
            + "Its price and its other local fields can still be edited. Nothing was saved.";

    /// <summary>Why a category may not be renamed locally.</summary>
    public static string CategoryNameRefusal(ExternalProvider? provider) =>
        $"This category's name is managed by {ProviderName(provider)} and cannot be changed here. "
            + "Nothing was saved.";

    /// <summary>
    /// The provider to name in a refusal. A row carrying an external id always belongs to a business
    /// whose <c>SalesProvider</c> is set - storing that provider's credentials is what records both -
    /// so the fallback describes a state this application cannot reach rather than guessing a
    /// provider's name.
    /// </summary>
    private static string ProviderName(ExternalProvider? provider) =>
        provider?.ToString() ?? "this business's sales provider";
}
