using Inventory.Application.Costing;
using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// Reads the pending inventory-cost rebuilds whose last attempt failed (issue #1103's
/// <c>PendingInventoryCostRebuilds</c>), with each product's current name. Both tables are
/// tenant-owned, so the central query filters keep the read inside the caller's business; nothing
/// here filters by business itself.
/// </summary>
public sealed class EfProductCostAttentionReader : IProductCostAttentionReader
{
    private readonly AppDbContext _db;

    public EfProductCostAttentionReader(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<ProductNeedingCostAttention>> ListAsync(CancellationToken cancellationToken)
    {
        var failed = await _db.PendingInventoryCostRebuilds.AsNoTracking()
            .Where(row => row.LastFailureCode != null)
            .Select(row => new { row.ProductId, row.LastFailureCode, row.AttemptCount, row.LastAttemptAtUtc, row.CreatedAtUtc, row.Id })
            .ToListAsync(cancellationToken);

        if (failed.Count == 0)
            return [];

        var productIds = failed.ConvertAll(row => row.ProductId);
        var names = await _db.Products.AsNoTracking()
            .Where(product => productIds.Contains(product.Id))
            .Select(product => new { product.Id, product.Name })
            .ToDictionaryAsync(product => product.Id, product => product.Name, cancellationToken);

        return failed
            .OrderBy(row => row.CreatedAtUtc)
            .ThenBy(row => row.Id)
            .Select(row => new ProductNeedingCostAttention(
                row.ProductId,
                names.TryGetValue(row.ProductId, out var name) ? name : null,
                row.LastFailureCode!,
                row.AttemptCount,
                row.LastAttemptAtUtc is { } attempted ? DateTime.SpecifyKind(attempted, DateTimeKind.Utc) : null))
            .ToList();
    }
}
