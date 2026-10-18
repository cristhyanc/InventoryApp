using Inventory.Application.Nayax;
using Inventory.Domain.Providers;
using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="INayaxProductLinkStore"/> (issue #733): since issue #759
/// it reads <c>Product.ExternalProductId</c>, the column that holds the business's sales provider's
/// id for the row, and nothing else.
///
/// <para><strong>Only while the business sells through Nayax.</strong> A business has zero or one
/// sales provider, and the column holds that provider's id without naming it, so the read asks for
/// the rows whose owning business's <c>SalesProvider</c> is <see cref="ExternalProvider.Nayax"/>
/// (<see cref="EfSalesProviderQueries.ProductsIdentifiedBy"/>) - in the same statement, so the lookup
/// is still one round trip on the request's scoped context (docs/architecture.md § Concurrency inside
/// one request). A business with no provider, or with another one, resolves no Nayax id at all - the
/// answer callers already handle as an unknown product - rather than reading some other system's
/// identifier as a Nayax one.</para>
///
/// <para>It replaced a read of the <c>ProductExternalIds</c> link table, which issue #759 stopped
/// writing; the lookup it answers, and every caller of it, is unchanged. The column is on the product
/// row itself, so there is no longer anything to join or any way for an id to name another business's
/// product.</para>
///
/// <para><c>AppDbContext</c>'s global tenant query filter scopes the product read the way it scopes
/// every other tenant-owned read; no per-call business filter is added here.</para>
/// </summary>
public sealed class EfNayaxProductLinkStore : INayaxProductLinkStore
{
    private readonly AppDbContext _db;

    public EfNayaxProductLinkStore(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyDictionary<long, long>> GetLocalProductIdsAsync(
        IReadOnlyCollection<long> nayaxProductIds, CancellationToken cancellationToken)
    {
        if (nayaxProductIds.Count == 0)
            return new Dictionary<long, long>();

        // The stored external id is opaque text matched exactly, so the lookup is translated into the
        // text form first (NayaxExternalId is the one place that conversion is written) rather than
        // by casting the column, which would neither use the unique index nor stay correct for a
        // provider whose ids are not numbers.
        var externalIds = nayaxProductIds.Distinct().Select(NayaxExternalId.From).ToList();

        // One statement: the rows whose business sells through Nayax and whose id is one of these.
        // A business with no provider, or another provider, matches nothing - the answer every
        // caller already treats as an unknown product - rather than having some other system's
        // identifier read as a Nayax one.
        var products = await EfSalesProviderQueries
            .ProductsIdentifiedBy(_db, ExternalProvider.Nayax)
            .Where(product => externalIds.Contains(product.ExternalProductId!))
            .Select(product => new { ExternalProductId = product.ExternalProductId!, product.Id })
            .ToListAsync(cancellationToken);

        var localProductIdByNayaxId = new Dictionary<long, long>(products.Count);
        foreach (var product in products)
        {
            // A stored id this helper could not have written is not a Nayax id, so it resolves to
            // nothing rather than to a nearby number; the filtered unique index on
            // (BusinessId, ExternalProductId) is what makes the remaining keys distinct.
            if (NayaxExternalId.TryParse(product.ExternalProductId, out var nayaxProductId))
                localProductIdByNayaxId[nayaxProductId] = product.Id;
        }

        return localProductIdByNayaxId;
    }
}
