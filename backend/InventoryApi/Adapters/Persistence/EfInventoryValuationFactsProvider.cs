using Inventory.Application.Reporting.Dashboard;
using InventoryApi.Data;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Adapters.Persistence;

/// <summary>
/// Temporary EF Core implementation of <see cref="IInventoryValuationFactsProvider"/>. It lives in
/// InventoryApi, not Inventory.Infrastructure, for the same reason the other
/// <c>Ef&lt;Feature&gt;ReportFactsProvider</c> adapters do: it depends on <see cref="AppDbContext"/>,
/// which still lives in InventoryApi. The context's global business query filter already scopes
/// this projection to the caller's business, so no per-call filter is needed here.
/// </summary>
public sealed class EfInventoryValuationFactsProvider : IInventoryValuationFactsProvider
{
    private readonly AppDbContext _db;

    public EfInventoryValuationFactsProvider(AppDbContext db) => _db = db;

    public async Task<IReadOnlyList<decimal?>> GetProductInventoryValuesAsync(CancellationToken cancellationToken) =>
        await _db.Products.AsNoTracking().Select(p => p.InventoryValue).ToListAsync(cancellationToken);
}
