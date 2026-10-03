using DomainProductMatcher = Inventory.Domain.Reporting.ProductMatching.ProductMatcher;
using Inventory.Domain.Reporting.ProductMatching;
using InventoryApi.Models;

namespace InventoryApi.Services;

/// <summary>
/// Thin wrapper over the deterministic <see cref="DomainProductMatcher"/> for the callers that
/// still operate on the persistence <see cref="Product"/> entity (machine service, inventory cost
/// rebuild, import, site commissions). The migrated product-profitability report and sale-costing
/// use cases (issue #297) call the Domain matcher directly on their own candidate projections
/// instead of through this wrapper, so all stay on one authoritative matching implementation.
/// </summary>
public static class NayaxProductMatcher
{
    public static Product? Match(IEnumerable<Product> products, long? nayaxProductId, string? nayaxProductName)
    {
        var catalogue = products as IReadOnlyCollection<Product> ?? products.ToArray();
        var matchedId = DomainProductMatcher.Match(
            catalogue.Select(product => new ProductMatchCandidate(product.Id, product.Name)),
            nayaxProductId, nayaxProductName);
        return matchedId.HasValue ? catalogue.SingleOrDefault(product => product.Id == matchedId.Value) : null;
    }

    public static string NormalizeName(string name) => DomainProductMatcher.NormalizeName(name);
}
