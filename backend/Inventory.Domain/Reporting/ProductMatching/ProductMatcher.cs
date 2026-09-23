namespace Inventory.Domain.Reporting.ProductMatching;

/// <summary>
/// The minimal catalogue shape the matching algorithm needs: an identity and a display name.
/// Deliberately not the persistence <c>Product</c> entity, so this deterministic rule has no EF
/// Core or InventoryApi dependency.
/// </summary>
public readonly record struct ProductMatchCandidate(long Id, string Name);

/// <summary>
/// Deterministic matching between a raw Nayax product identifier/name and the product catalogue.
/// Matches by ID first; when no ID match exists, falls back to matching the name with any Nayax
/// price/code suffix in parentheses stripped, but only when exactly one catalogue product
/// normalizes to that name (an ambiguous or absent name match is unmapped).
/// </summary>
public static class ProductMatcher
{
    public static long? Match(IEnumerable<ProductMatchCandidate> candidates, long? nayaxProductId, string? nayaxProductName)
    {
        var catalogue = candidates as IReadOnlyCollection<ProductMatchCandidate> ?? candidates.ToArray();
        ProductMatchCandidate? byId = nayaxProductId.HasValue
            ? catalogue.Where(candidate => candidate.Id == nayaxProductId.Value)
                .Select(candidate => (ProductMatchCandidate?)candidate)
                .SingleOrDefault()
            : null;
        if (byId is not null || string.IsNullOrWhiteSpace(nayaxProductName))
            return byId?.Id;

        var normalizedName = NormalizeName(nayaxProductName);
        return catalogue
            .Where(candidate => string.Equals(NormalizeName(candidate.Name), normalizedName, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray() switch
        {
            [var candidate] => candidate.Id,
            _ => null
        };
    }

    public static string NormalizeName(string name)
    {
        var parenthesis = name.IndexOf('(');
        return (parenthesis >= 0 ? name[..parenthesis] : name).Trim();
    }
}
