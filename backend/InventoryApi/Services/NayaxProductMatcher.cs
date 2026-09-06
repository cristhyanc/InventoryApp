using InventoryApi.Models;

namespace InventoryApi.Services;

public static class NayaxProductMatcher
{
    public static Product? Match(IEnumerable<Product> products, long? nayaxProductId, string? nayaxProductName)
    {
        var catalogue = products as IReadOnlyCollection<Product> ?? products.ToArray();
        var byId = nayaxProductId.HasValue
            ? catalogue.SingleOrDefault(product => product.Id == nayaxProductId.Value)
            : null;
        if (byId is not null || string.IsNullOrWhiteSpace(nayaxProductName))
            return byId;

        var normalizedName = NormalizeName(nayaxProductName);
        return catalogue
            .Where(product => string.Equals(NormalizeName(product.Name), normalizedName, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray() switch
            {
                [var product] => product,
                _ => null
            };
    }

    public static string NormalizeName(string name)
    {
        var parenthesis = name.IndexOf('(');
        return (parenthesis >= 0 ? name[..parenthesis] : name).Trim();
    }
}
