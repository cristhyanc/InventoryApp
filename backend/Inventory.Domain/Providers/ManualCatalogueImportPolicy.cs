using System.Globalization;

namespace Inventory.Domain.Providers;

/// <summary>
/// Whether a provider's catalogue may be imported into a business that maintains its own catalogue
/// (issue #736, parent #329 decision P2).
///
/// <para><strong>A manual catalogue and a provider import cannot be merged by guessing.</strong>
/// Nothing links a product the operator created to the provider's product of the same name - the
/// import identifies a row only by the provider's id recorded on it (docs/architecture.md § Nayax
/// product catalogue import), so for a business holding products with no provider id the import
/// would create a second row for every one of them. Until the catalogue review ships (issue #738
/// for the API, #739 for the screen), where a person links or creates each provider product
/// deliberately, the import is refused instead.</para>
///
/// <para>A business whose products all carry a provider id imports exactly as before: the refusal
/// is about the rows the import cannot recognise, not about the import.</para>
/// </summary>
public static class ManualCatalogueImportPolicy
{
    /// <summary>
    /// The refusal message, or <c>null</c> when the import may run.
    /// </summary>
    /// <param name="productsWithNoProviderId">
    /// How many of the business's products carry no external product id.
    /// </param>
    public static string? Refusal(int productsWithNoProviderId) =>
        productsWithNoProviderId <= 0
            ? null
            : $"The product catalogue cannot be imported while this business has "
                + $"{Products(productsWithNoProviderId)} that the provider does not manage: the "
                + "import identifies a product only by the provider's own id, so it would create a "
                + "duplicate of each of them. Nothing was imported.";

    private static string Products(int count) =>
        count == 1
            ? "1 product"
            : $"{count.ToString(CultureInfo.InvariantCulture)} products";
}
