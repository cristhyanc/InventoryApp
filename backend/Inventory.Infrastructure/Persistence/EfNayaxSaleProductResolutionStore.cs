using System.Globalization;
using Inventory.Application.SaleProductResolution;
using Inventory.Application.Time;
using Inventory.Domain.Reporting.ProductMatching;
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
/// decision fills the sale's <c>ProductId</c> only while it is still null, so a rerun writes nothing
/// and no sale is ever moved off a product it already carries. That is one guarded statement rather
/// than a value read earlier - a comparison a concurrent save could land between would make the
/// invariant a hope - and the audit row commits with it in the same transaction or not at all.
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

    public async Task<IReadOnlyList<ProductMatchCandidate>> GetProductCandidatesAsync(
        CancellationToken cancellationToken) =>
        (await _db.Products.AsNoTracking()
                .Select(product => new { product.Id, product.Name })
                .ToListAsync(cancellationToken))
            .ConvertAll(product => new ProductMatchCandidate(product.Id, product.Name));

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

    public async Task<IReadOnlyList<long>> ApplyDecisionsAsync(
        IReadOnlyCollection<NayaxSaleProductDecision> decisions,
        string decidedBy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        if (decisions.Count == 0)
            return [];

        // The read-only support-session rule, for the ExecuteUpdate below that never reaches
        // BusinessOwnershipEnforcer (issue #681), exactly as EfNayaxConnectionStore does it.
        SetBasedWriteEnforcer.RequireWritableScope(_db);

        // One transaction over every claim and every audit row: a decision that settled a sale and
        // the evidence of who settled it commit together, and a failure part way through leaves
        // neither. A caller that already opened one owns the commit, as elsewhere in this family.
        await using var transaction = _db.Database.IsRelational() && _db.Database.CurrentTransaction is null
            ? await _db.Database.BeginTransactionAsync(cancellationToken)
            : null;

        var settled = new List<long>();

        // Built up and added after the loop, never between two claims: an ExecuteUpdate is a
        // statement of its own and must not run with tracked changes of ours waiting behind it.
        var audit = new List<NayaxSaleProductResolution>();

        foreach (var decision in decisions)
        {
            // The guarded fill-if-null, as one statement the database evaluates: the expected null
            // is part of the UPDATE's own predicate, not a value this process read beforehand, so
            // two overlapping runs cannot both believe the sale is unresolved. Zero rows means
            // somebody else decided it first, and nothing at all is written for it - not the
            // product, and not an audit row that would claim the previous value was null.
            var claimed = await _db.NayaxSales
                .Where(sale => sale.Id == decision.SaleId && sale.ProductId == null)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(sale => sale.ProductId, decision.ProductId),
                    cancellationToken);
            if (claimed == 0)
                continue;

            // Read after the claim, untracked: ExecuteUpdate does not go through the change
            // tracker, so nothing here may rely on an entity loaded before it.
            var sale = await _db.NayaxSales.AsNoTracking()
                .Where(x => x.Id == decision.SaleId)
                .Select(x => new { x.TransactionID })
                .SingleAsync(cancellationToken);

            var conflict = await _db.NayaxSaleProductConflicts.AsNoTracking()
                .FirstOrDefaultAsync(x => x.NayaxSaleId == decision.SaleId, cancellationToken);

            audit.Add(new NayaxSaleProductResolution
            {
                NayaxSaleId = decision.SaleId,
                TransactionId = sale.TransactionID,
                // Provably the previous value: the statement above refused to fill anything else.
                PreviousProductId = null,
                NewProductId = decision.ProductId,
                Rule = NayaxSaleProductResolutionRule.OperatorDecision,
                CandidateProductIds = conflict is null
                    ? null
                    : string.Create(
                        CultureInfo.InvariantCulture,
                        $"{conflict.DirectCandidateProductId},{conflict.ExternalCandidateProductId}"),
                DecidedBy = decidedBy,
                ResolvedAtUtc = _clock.UtcNow,
            });

            settled.Add(decision.SaleId);
        }

        if (audit.Count > 0)
        {
            _db.NayaxSaleProductResolutions.AddRange(audit);
            await _db.SaveChangesAsync(cancellationToken);
        }

        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        return settled;
    }
}
