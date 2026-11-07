using Inventory.Application.Nayax;
using Inventory.Domain.Reporting.ProductMatching;
using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The tenant-scoped resolution of a Nayax sale's local product (issues #745 and #1105): the
/// business's provider link map turns the raw Nayax product id into a local product id, and
/// <see cref="ProductMatcher.MatchLinked"/> then decides, falling back to the product name. It is the
/// one resolution a new sale (<see cref="EfLatestNayaxSalesStore"/>) and an Owner's "Use Nayax's
/// values" correction (<see cref="EfNayaxSaleReviewStore"/>) both use, so the two can never resolve
/// the same provider product differently. A Nayax product id is never used as a local product id.
/// </summary>
internal static class NayaxSaleProductResolver
{
    /// <summary>The business's products, as the matcher's candidates.</summary>
    public static async Task<List<ProductMatchCandidate>> ReadCandidatesAsync(
        AppDbContext db, CancellationToken cancellationToken) =>
        (await db.Products.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(product => new ProductMatchCandidate(product.Id, product.Name))
            .ToList();

    /// <summary>
    /// The local product for one provider product, given candidates and a link map already read.
    /// </summary>
    public static long? Match(
        IReadOnlyList<ProductMatchCandidate> candidates,
        IReadOnlyDictionary<long, long> localProductIdByNayaxId,
        long? nayaxProductId,
        string? productName) =>
        ProductMatcher.MatchLinked(
            candidates,
            LocalProductId(nayaxProductId, localProductIdByNayaxId),
            productName);

    /// <summary>
    /// Reads what is needed and resolves one provider product, or <see langword="null"/> when this
    /// business links it to no product and no product name matches.
    /// </summary>
    public static async Task<long?> ResolveAsync(
        AppDbContext db,
        INayaxProductLinkStore productLinks,
        long? nayaxProductId,
        string? productName,
        CancellationToken cancellationToken)
    {
        var candidates = await ReadCandidatesAsync(db, cancellationToken).ConfigureAwait(false);

        IReadOnlyDictionary<long, long> links = nayaxProductId is { } id
            ? await productLinks.GetLocalProductIdsAsync([id], cancellationToken).ConfigureAwait(false)
            : new Dictionary<long, long>();

        return Match(candidates, links, nayaxProductId, productName);
    }

    /// <summary>
    /// The local product a Nayax product id names, or <see langword="null"/> when this business has
    /// recorded that Nayax id on no product - never the product whose local id happens to equal the
    /// remote one (issue #745).
    /// </summary>
    public static long? LocalProductId(
        long? nayaxProductId, IReadOnlyDictionary<long, long> localProductIdByNayaxId) =>
        nayaxProductId is long id && localProductIdByNayaxId.TryGetValue(id, out var localProductId)
            ? localProductId
            : null;
}
