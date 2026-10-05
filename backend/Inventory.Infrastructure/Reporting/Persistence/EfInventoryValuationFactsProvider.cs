using Inventory.Application.Reporting.Dashboard;
using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Reporting.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IInventoryValuationFactsProvider"/>. It lives in
/// Inventory.Infrastructure for the same reason the other <c>Ef&lt;Feature&gt;ReportFactsProvider</c>
/// adapters do: it depends on <see cref="AppDbContext"/>, which moved here in issue #307 ahead of
/// this adapter family (issue #308, Persistence 7/8 of #153). The context's global business query
/// filter already scopes this projection to the caller's business, so no per-call filter is needed
/// here.
/// </summary>
public sealed class EfInventoryValuationFactsProvider : IInventoryValuationFactsProvider
{
    private readonly AppDbContext _db;

    public EfInventoryValuationFactsProvider(AppDbContext db) => _db = db;

    public async Task<IReadOnlyList<decimal?>> GetProductInventoryValuesAsync(CancellationToken cancellationToken) =>
        await _db.Products.AsNoTracking().Select(p => p.InventoryValue).ToListAsync(cancellationToken);
}
