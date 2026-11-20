using Inventory.Application.CatalogueReview;
using Inventory.Domain.Providers;
using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IUnimportedProviderProductReader"/> (issue #1352): the
/// provider products this business is selling that no product of its own carries the id of.
///
/// <para><strong>The sale filter is the catalogue review's, deliberately shared in wording with
/// <c>EfCatalogueReviewStore.UnresolvedSaleCountsAsync</c>.</strong> A sale is evidence that a
/// product is missing locally only when it has no local product, came from Nayax, carries a provider
/// product id, and that id's provenance is known to be raw. A provenance-unknown row's stored number
/// may be either a raw provider id or the local product id the old synchronization wrote, so it is
/// evidence about nothing; counting one would send an operator to import a product that is already
/// there. The window is the caller's, derived once from the business calendar in
/// <see cref="GetUnimportedProviderProductAttention"/>.</para>
///
/// <para><strong>Identity comes from the external id column, never from a key or a name.</strong>
/// Whether a product is already imported is decided by comparing
/// <c>Product.ExternalProductId</c> with the provider id's own <c>ExternalId</c> text
/// (<see cref="NayaxExternalId"/>); a local product key that equals a provider id is a coincidence
/// (issue #735) and a product that merely shares the sale's name is a different row. The comparison
/// is made in memory over this business's stored external ids rather than by casting a number to text
/// inside SQL, because that text is matched byte for byte and the one conversion that produces it
/// lives in the Domain.</para>
///
/// <para><strong>Retired products count as imported.</strong> The products read here are every
/// product of the business, active or not: a retired row still carries the provider's id, so the
/// product <em>is</em> in the business and asking for it to be imported again would be wrong.</para>
///
/// <para><strong>It writes nothing and touches no sale.</strong> Linking a provider id to a product
/// is what clears this read; the unresolved sales themselves stay unresolved until the Owner's own
/// audited resolution step runs. Scoped to the caller's business by the central
/// <c>AppDbContext</c> query filters alone - no business predicate of this adapter's own.</para>
/// </summary>
public sealed class EfUnimportedProviderProductReader : IUnimportedProviderProductReader
{
    private readonly AppDbContext _db;

    public EfUnimportedProviderProductReader(AppDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task<UnimportedProviderProductAttention> ReadAsync(
        DateTime fromUtc, DateTime toUtc, int maxExamples, CancellationToken cancellationToken)
    {
        // One group per provider product id sold in the window, with the instant of its most recent
        // qualifying sale - which is both what orders the examples and what identifies the sale the
        // name is taken from.
        var sold = await QualifyingSales(fromUtc, toUtc)
            .GroupBy(sale => sale.NayaxProductId!.Value)
            .Select(grouped => new
            {
                NayaxProductId = grouped.Key,
                LatestSaleAtUtc = grouped.Max(sale => sale.MachineAuthorizationTime),
            })
            .ToListAsync(cancellationToken);

        if (sold.Count == 0)
        {
            return new UnimportedProviderProductAttention(0, []);
        }

        var imported = await ImportedProviderProductIdsAsync(cancellationToken);

        var missing = sold
            .Where(product => !imported.Contains(product.NayaxProductId))
            .OrderByDescending(product => product.LatestSaleAtUtc)
            // Stable beyond the instant, so two products last sold in the same second are always
            // named in the same order.
            .ThenBy(product => product.NayaxProductId)
            .ToList();

        var examples = new List<UnimportedProviderProductExample>();
        var named = missing
            .Take(Math.Max(0, maxExamples))
            .Select(product => product.NayaxProductId);

        // Asked one at a time, in order: the names come from this context's own queries, which may
        // not run concurrently.
        foreach (var nayaxProductId in named)
        {
            examples.Add(new UnimportedProviderProductExample(
                NayaxExternalId.From(nayaxProductId),
                await LatestStoredNameAsync(nayaxProductId, fromUtc, toUtc, cancellationToken)));
        }

        return new UnimportedProviderProductAttention(missing.Count, examples);
    }

    /// <summary>
    /// The sales that are evidence a provider product is missing locally: the catalogue review's own
    /// unresolved-sale filter, bounded to the caller's window.
    /// </summary>
    private IQueryable<Models.NayaxSales> QualifyingSales(DateTime fromUtc, DateTime toUtc) =>
        _db.NayaxSales
            .AsNoTracking()
            .Where(sale =>
                sale.ProductId == null
                && sale.ProviderProductIdIsRaw
                && sale.Provider == ExternalProvider.Nayax
                && sale.NayaxProductId != null
                && sale.MachineAuthorizationTime >= fromUtc
                && sale.MachineAuthorizationTime <= toUtc);

    /// <summary>
    /// The Nayax product ids this business already holds a product for, read from the external id
    /// column and parsed through the one conversion that writes it. Text that conversion could not
    /// have produced is not a Nayax id and names no product here.
    /// </summary>
    private async Task<HashSet<long>> ImportedProviderProductIdsAsync(CancellationToken cancellationToken)
    {
        var externalIds = await _db.Products
            .AsNoTracking()
            .Where(product => product.ExternalProductId != null)
            .Select(product => product.ExternalProductId!)
            .ToListAsync(cancellationToken);

        var imported = new HashSet<long>();
        foreach (var externalId in externalIds)
        {
            if (NayaxExternalId.TryParse(externalId, out var nayaxProductId))
            {
                imported.Add(nayaxProductId);
            }
        }

        return imported;
    }

    /// <summary>
    /// The product name the most recent qualifying sale of one provider product carried, or
    /// <see langword="null"/> when that sale stored none. Asked per named example - at most
    /// <see cref="UnimportedProviderProductAttentionPolicy.MaxExamples"/> of them - rather than by
    /// reading every sale of the window into memory to pick three names out of it.
    /// </summary>
    private Task<string?> LatestStoredNameAsync(
        long nayaxProductId, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken) =>
        QualifyingSales(fromUtc, toUtc)
            .Where(sale => sale.NayaxProductId == nayaxProductId)
            .OrderByDescending(sale => sale.MachineAuthorizationTime)
            // The key breaks a tie between two sales of one product at the same instant, so the name
            // shown does not depend on the order the database happened to return them in.
            .ThenByDescending(sale => sale.Id)
            .Select(sale => sale.ProductName)
            .FirstOrDefaultAsync(cancellationToken);
}
