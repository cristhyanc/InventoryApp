using System.Globalization;
using Inventory.Application.SaleProductResolution;
using Inventory.Application.Time;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="INayaxSaleProductResolutionStore"/> (issue #758),
/// beside the rest of the adapter family and <see cref="AppDbContext"/>.
///
/// Tenant isolation is the central query filter's and the ownership enforcer's, as everywhere else:
/// there is no per-call <c>BusinessId</c> predicate here. That is also what makes a decision naming
/// another business's sale or product impossible to apply - the sale is not in the caller's
/// unresolved list and the product is not in the caller's catalogue - before the composite foreign
/// key on <c>(BusinessId, ProductId)</c> refuses it a second time at the database.
///
/// It applies no rule of its own beyond the one invariant that makes the operation idempotent: a
/// decision is staged only while the sale's <c>ProductId</c> is still null, so a rerun writes
/// nothing and no sale is ever moved off a product it already carries.
/// </summary>
public sealed class EfNayaxSaleProductResolutionStore : INayaxSaleProductResolutionStore
{
    private readonly AppDbContext _db;
    private readonly IClock _clock;

    public EfNayaxSaleProductResolutionStore(AppDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<IReadOnlyList<UnresolvedNayaxSale>> ListUnresolvedAsync(CancellationToken cancellationToken)
    {
        var conflicts = await _db.NayaxSaleProductConflicts.AsNoTracking()
            .ToDictionaryAsync(conflict => conflict.NayaxSaleId, cancellationToken);

        var sales = await _db.NayaxSales.AsNoTracking()
            .Where(sale => sale.ProductId == null)
            .OrderBy(sale => sale.MachineAuthorizationTime)
            .ThenBy(sale => sale.Id)
            .Select(sale => new
            {
                sale.Id,
                sale.TransactionID,
                sale.MachineID,
                sale.MachineAuthorizationTime,
                sale.ProductName,
                sale.NayaxProductId,
                sale.ProviderProductIdIsRaw,
                // "Costed today" in the same terms the release's failure gate uses: a recorded
                // status or a recorded amount. Either one means the sale is carrying COGS that a
                // future replay would stop seeing if it stayed unresolved.
                IsCosted = sale.CostingStatus == SaleCostingStatus.Costed
                    || sale.CostingStatus == SaleCostingStatus.LegacyEstimated
                    || sale.UnitCostAtSale != null
                    || sale.CostOfGoodsSold != null,
            })
            .ToListAsync(cancellationToken);

        return sales.ConvertAll(sale =>
        {
            conflicts.TryGetValue(sale.Id, out var conflict);
            return new UnresolvedNayaxSale(
                sale.Id,
                sale.TransactionID,
                sale.MachineID,
                sale.MachineAuthorizationTime,
                sale.ProductName,
                // A provenance-unknown number is not reported as the provider's product id,
                // because nothing knows that it is one.
                sale.ProviderProductIdIsRaw ? sale.NayaxProductId : null,
                sale.IsCosted,
                conflict?.DirectCandidateProductId,
                conflict?.ExternalCandidateProductId);
        });
    }

    public async Task<IReadOnlyDictionary<long, string>> GetProductNamesAsync(
        IReadOnlyCollection<long> productIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(productIds);
        if (productIds.Count == 0)
            return new Dictionary<long, string>();

        var ids = productIds as List<long> ?? productIds.ToList();
        return await _db.Products.AsNoTracking()
            .Where(product => ids.Contains(product.Id))
            .ToDictionaryAsync(product => product.Id, product => product.Name, cancellationToken);
    }

    public async Task<bool> StageDecisionAsync(
        long saleId, long productId, string decidedBy, CancellationToken cancellationToken)
    {
        var sale = await _db.NayaxSales
            .FirstOrDefaultAsync(x => x.Id == saleId && x.ProductId == null, cancellationToken);
        if (sale is null)
            return false;

        var conflict = await _db.NayaxSaleProductConflicts.AsNoTracking()
            .FirstOrDefaultAsync(x => x.NayaxSaleId == saleId, cancellationToken);

        _db.NayaxSaleProductResolutions.Add(new NayaxSaleProductResolution
        {
            NayaxSaleId = sale.Id,
            TransactionId = sale.TransactionID,
            PreviousProductId = null,
            NewProductId = productId,
            Rule = NayaxSaleProductResolutionRule.OperatorDecision,
            CandidateProductIds = conflict is null
                ? null
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"{conflict.DirectCandidateProductId},{conflict.ExternalCandidateProductId}"),
            DecidedBy = decidedBy,
            ResolvedAtUtc = _clock.UtcNow,
        });

        sale.ProductId = productId;
        return true;
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) => _db.SaveChangesAsync(cancellationToken);
}
