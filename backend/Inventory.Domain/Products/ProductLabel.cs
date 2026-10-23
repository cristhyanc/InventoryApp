using System.Globalization;

namespace Inventory.Domain.Products;

/// <summary>
/// The one way a product is named in a caller-facing message (issue #833).
///
/// A refused purchase, sales import or costing repair reaches the operator as the message text
/// alone, so it names the product it is about - "Product Coke (12) becomes cost-negative at
/// adjustment 345." - rather than an ID they then have to look up. The ID stays in every message,
/// because it is what identifies the row unambiguously when two products share a name.
///
/// A product whose name is not known - a caller that never loaded one, or a blank stored name -
/// falls back to the bare "Product 12"/"product 12" wording these messages have always used,
/// rather than printing an empty pair of brackets.
/// </summary>
public static class ProductLabel
{
    /// <summary>The product at the start of a sentence: <c>Product Coke (12)</c>.</summary>
    public static string Sentence(long productId, string? productName) =>
        $"Product {Identify(productId, productName)}";

    /// <summary>The product inside a sentence: <c>product Coke (12)</c>.</summary>
    public static string Phrase(long productId, string? productName) =>
        $"product {Identify(productId, productName)}";

    private static string Identify(long productId, string? productName)
    {
        var id = productId.ToString(CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(productName) ? id : $"{productName} ({id})";
    }
}
