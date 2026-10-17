using Inventory.Application.Nayax;
using Inventory.Domain.Providers;
using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="INayaxProductLinkStore"/> (issue #733): it reads the
/// <c>ProductExternalIds</c> link table issue #732 created, and nothing else.
///
/// <para>It never touches <c>Products</c>. A link already carries the local product id, and the
/// foreign key behind it - <c>(BusinessId, ProductId)</c> against the <c>(BusinessId, Id)</c>
/// alternate key - is what guarantees the product exists and belongs to the same business, so a
/// join would only re-check what the database already enforces.</para>
///
/// <para><c>AppDbContext</c>'s global tenant query filter scopes this read the way it scopes every
/// other tenant-owned read; no per-call business filter is added here.</para>
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

        // The stored ExternalId is opaque text matched exactly, so the lookup is translated into the
        // text form first (NayaxExternalId is the one place that conversion is written) rather than
        // by casting the column, which would neither use the unique index nor stay correct for a
        // provider whose ids are not numbers.
        var externalIds = nayaxProductIds.Distinct().Select(NayaxExternalId.From).ToList();

        var links = await _db.ProductExternalIds
            .AsNoTracking()
            .Where(link => link.Provider == ExternalProvider.Nayax && externalIds.Contains(link.ExternalId))
            .Select(link => new { link.ExternalId, link.ProductId })
            .ToListAsync(cancellationToken);

        var localProductIdByNayaxId = new Dictionary<long, long>(links.Count);
        foreach (var link in links)
        {
            // A stored id this helper could not have written is not a Nayax id, so it resolves to
            // nothing rather than to a nearby number; the uniqueness rule on
            // (BusinessId, Provider, ExternalId) is what makes the remaining keys distinct.
            if (NayaxExternalId.TryParse(link.ExternalId, out var nayaxProductId))
                localProductIdByNayaxId[nayaxProductId] = link.ProductId;
        }

        return localProductIdByNayaxId;
    }
}
