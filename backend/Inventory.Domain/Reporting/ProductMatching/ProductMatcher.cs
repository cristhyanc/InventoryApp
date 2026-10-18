namespace Inventory.Domain.Reporting.ProductMatching;

/// <summary>
/// The minimal catalogue shape the matching algorithm needs: an identity and a display name.
/// Deliberately not the persistence <c>Product</c> entity, so this deterministic rule has no EF
/// Core or InventoryApi dependency.
/// </summary>
public readonly record struct ProductMatchCandidate(long Id, string Name);

/// <summary>
/// Deterministic matching between an already-resolved local product id, the provider's reported
/// product name and the product catalogue. Matches by that id first; when it names no catalogue row,
/// falls back to matching the name with any Nayax price/code suffix in parentheses stripped, but
/// only when exactly one catalogue product normalizes to that name (an ambiguous or absent name
/// match is unmapped).
///
/// Since issue #735 (A4) nothing here takes a <em>provider's</em> product id: a local key and a
/// Nayax id that are the same number are unrelated, so a Nayax id is resolved through
/// <c>Product.ExternalProductId</c> before it reaches this rule.
/// </summary>
public static class ProductMatcher
{
    /// <summary>
    /// Matches a Nayax product id that has already been resolved through the provider identity
    /// recorded on the local row (issue #745, parent #329 decision A2b;
    /// <c>Product.ExternalProductId</c> since issue #759): <paramref name="linkedProductId"/> is the
    /// <em>local</em> product id that recorded id named, or <see langword="null"/> when the business
    /// has that Nayax id on no product.
    ///
    /// <para>This is the only id-bearing entry point, and it takes a local id. A caller holding a raw
    /// Nayax product id resolves it through <c>INayaxProductLinkStore</c> first; passing one here
    /// would compare a provider's number with <c>Product.Id</c>, which issue #735 (A4) made a
    /// coincidence - the Nayax-id overload that existed for the paths migrated in #733/#745/#758 is
    /// gone with it, so there is nothing left to pass one to
    /// (<c>ProviderIdIsNeverALocalKeyTests</c>).</para>
    ///
    /// <para>A resolved local product the catalogue holds is the match and short-circuits the name
    /// fallback, exactly as an id match always has; an unresolved Nayax id is an unknown identity and
    /// falls back to the unambiguous name match, exactly as an id no catalogue row carried always
    /// has. A <see langword="null"/> id with a name is the name rule on its own, which is how the
    /// sale-resolution report (issue #758) asks whether exactly one product's name attributes a
    /// sale.</para>
    /// </summary>
    public static long? MatchLinked(
        IEnumerable<ProductMatchCandidate> candidates, long? linkedProductId, string? nayaxProductName)
    {
        // The resolved id is a local product id, so the same "which catalogue row carries this id?"
        // step is the right one; it is the *input* that stopped being a remote identifier.
        var catalogue = AsCatalogue(candidates);
        return MatchById(catalogue, linkedProductId, nayaxProductName);
    }

    private static long? MatchById(
        IReadOnlyCollection<ProductMatchCandidate> catalogue, long? productId, string? nayaxProductName)
    {
        ProductMatchCandidate? byId = productId.HasValue
            ? catalogue.Where(candidate => candidate.Id == productId.Value)
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

    private static IReadOnlyCollection<ProductMatchCandidate> AsCatalogue(IEnumerable<ProductMatchCandidate> candidates) =>
        candidates as IReadOnlyCollection<ProductMatchCandidate> ?? candidates.ToArray();

    public static string NormalizeName(string name)
    {
        var parenthesis = name.IndexOf('(');
        return (parenthesis >= 0 ? name[..parenthesis] : name).Trim();
    }
}
