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
    /// <summary>
    /// Matches a Nayax product identifier against the catalogue's own ids, which is only correct
    /// while <c>Product.Id</c> *is* the Nayax product id.
    ///
    /// <para>A caller that holds a Nayax id coming from Nayax data resolves it through the recorded
    /// provider id first and uses <see cref="MatchLinked"/> instead (issues #733, #745 and #759).
    /// What is still matched here is a <em>stored</em> <c>NayaxSales.NayaxProductId</c>
    /// value, which the latest-sales synchronization writes as the local product id it matched, so
    /// re-resolving it as a Nayax id would turn a known local product into an unknown one: the
    /// costing, timestamp-repair and reporting paths that read that column are #747 (A2c) and #734
    /// (A3).</para>
    /// </summary>
    public static long? Match(IEnumerable<ProductMatchCandidate> candidates, long? nayaxProductId, string? nayaxProductName)
    {
        var catalogue = AsCatalogue(candidates);
        return MatchById(catalogue, nayaxProductId, nayaxProductName);
    }

    /// <summary>
    /// The same matching rule for a Nayax product id that has already been resolved through the
    /// provider identity recorded on the local row (issue #745, parent #329 decision A2b;
    /// <c>Product.ExternalProductId</c> since issue #759): <paramref name="linkedProductId"/> is the
    /// <em>local</em> product id that recorded id named, or <see langword="null"/> when the business
    /// has that Nayax id on no product.
    ///
    /// <para>Only the identity step changes. A resolved local product the catalogue holds is the
    /// match and short-circuits the name fallback, exactly as an id match always has; an unresolved
    /// Nayax id is an unknown identity and falls back to the unambiguous name match, exactly as an id
    /// no catalogue row carried always has. What it can no longer do is match the local product whose
    /// id happens to equal the Nayax id - after #735 (A4) that is a coincidence, and before it the
    /// #732 (A1) backfill and the #759 copy mean every id this resolves resolves to the same row it
    /// did before.</para>
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
