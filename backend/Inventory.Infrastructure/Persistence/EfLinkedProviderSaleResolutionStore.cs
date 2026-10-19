using Inventory.Application.CatalogueReview;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.Providers;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="ILinkedProviderSaleResolutionStore"/> (issue #738), beside
/// the rest of the adapter family and <see cref="AppDbContext"/>.
///
/// <para>Tenant isolation is the central query filter's and the ownership enforcer's, as everywhere
/// else: there is no per-call <c>BusinessId</c> predicate here, and a sale of another business is
/// simply not in the read - before the composite foreign key on <c>(BusinessId, ProductId)</c>
/// refuses it a second time at the database.</para>
///
/// <para><strong>The provider id is text on the product and a number on the sale, and the conversion
/// goes one way only.</strong> <c>Product.ExternalProductId</c> is opaque text (issue #759) and
/// <c>NayaxSales.NayaxProductId</c> is the provider's own numeric id (issue #758), so the product's id
/// is parsed back through <c>NayaxExternalId</c> - the one conversion - and the sales are matched on
/// that number. Nothing here reads either value as a local product key, and an external id that is not
/// a Nayax id resolves no sale rather than resolving a nearby one.</para>
/// </summary>
public sealed class EfLinkedProviderSaleResolutionStore : ILinkedProviderSaleResolutionStore
{
    private readonly AppDbContext _db;

    public EfLinkedProviderSaleResolutionStore(AppDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task<ICatalogueReviewTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
        new CatalogueReviewTransaction(_db.Database.IsRelational() && _db.Database.CurrentTransaction is null
            ? await _db.Database.BeginTransactionAsync(cancellationToken)
            : null);

    /// <inheritdoc />
    public async Task<IReadOnlyList<UnresolvedLinkedSaleGroup>> ListAsync(CancellationToken cancellationToken)
    {
        await RequireNayaxSalesProviderAsync(cancellationToken);

        var linked = await _db.Products
            .AsNoTracking()
            .Where(product => product.ExternalProductId != null)
            .OrderBy(product => product.Id)
            .Select(product => new
            {
                LocalRowId = product.Id,
                product.Name,
                ProviderId = product.ExternalProductId!,
            })
            .ToListAsync(cancellationToken);

        var products = new List<(long LocalRowId, string Name, string ProviderId, long ProviderNumber)>();
        foreach (var product in linked)
            if (NayaxExternalId.TryParse(product.ProviderId, out var providerNumber))
                products.Add((product.LocalRowId, product.Name, product.ProviderId, providerNumber));

        if (products.Count == 0)
            return [];

        var providerNumbers = products.Select(product => product.ProviderNumber).Distinct().ToList();
        var sales = await UnresolvedSales(providerNumbers)
            .Select(sale => new
            {
                ProviderNumber = sale.NayaxProductId!.Value,
                sale.TransactionStatusId,
                sale.MachineAuthorizationTime,
            })
            .ToListAsync(cancellationToken);

        return products
            .Select(product =>
            {
                var own = sales
                    .Where(sale => sale.ProviderNumber == product.ProviderNumber)
                    .ToList();
                var completed = own
                    .Where(sale => NayaxTransactionStatusClassifier.IsCompletedSale(sale.TransactionStatusId))
                    .Select(sale => sale.MachineAuthorizationTime)
                    .ToList();

                return new UnresolvedLinkedSaleGroup(
                    product.LocalRowId,
                    product.Name,
                    product.ProviderId,
                    own.Count,
                    completed.Count == 0 ? null : completed.Min());
            })
            .Where(group => group.SaleCount > 0)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<long, int>> ResolveAsync(
        IReadOnlyCollection<UnresolvedLinkedSaleGroup> groups,
        string resolvedBy,
        DateTime resolvedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(groups);
        await RequireNayaxSalesProviderAsync(cancellationToken);

        if (groups.Count == 0)
            return new Dictionary<long, int>();

        // The read-only support-session rule, for the ExecuteUpdate below that never reaches
        // BusinessOwnershipEnforcer (issue #681), exactly as EfNayaxSaleProductResolutionStore does it.
        SetBasedWriteEnforcer.RequireWritableScope(_db);

        var settled = new Dictionary<long, int>();

        // Built up and added after every claim, never between two of them: an ExecuteUpdate is a
        // statement of its own and must not run with tracked changes of ours waiting behind it.
        var audit = new List<NayaxSaleProductResolution>();

        foreach (var group in groups)
        {
            if (!NayaxExternalId.TryParse(group.ProviderProductId, out var providerNumber))
                continue;

            var candidates = await UnresolvedSales([providerNumber])
                .OrderBy(sale => sale.Id)
                .Select(sale => new { sale.Id, sale.TransactionID })
                .ToListAsync(cancellationToken);

            foreach (var candidate in candidates)
            {
                // The guarded fill-if-null, as one statement the database evaluates: the expected
                // null is part of the UPDATE's own predicate rather than a value this process read
                // beforehand, so two overlapping runs cannot both believe the sale is unresolved.
                // Zero rows means somebody else decided it first, and nothing at all is written for
                // it - not the product, and not an audit row claiming the previous value was null.
                var claimed = await _db.NayaxSales
                    .Where(sale => sale.Id == candidate.Id && sale.ProductId == null)
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(sale => sale.ProductId, group.ProductId),
                        cancellationToken);
                if (claimed == 0)
                    continue;

                settled[group.ProductId] = settled.GetValueOrDefault(group.ProductId) + 1;
                audit.Add(new NayaxSaleProductResolution
                {
                    NayaxSaleId = candidate.Id,
                    TransactionId = candidate.TransactionID,
                    // Provably the previous value: the statement above refused to fill anything else.
                    PreviousProductId = null,
                    NewProductId = group.ProductId,
                    Rule = NayaxSaleProductResolutionRule.ProviderCatalogueLink,
                    // One candidate by definition - the provider id the product now carries - so
                    // there is no disagreement to record.
                    CandidateProductIds = null,
                    DecidedBy = resolvedBy,
                    ResolvedAtUtc = resolvedAtUtc,
                });
            }
        }

        _db.NayaxSaleProductResolutions.AddRange(audit);
        return settled;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<long, DateTime>> GetTransitionCutoffsAsync(
        IReadOnlyCollection<long> productIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(productIds);
        if (productIds.Count == 0)
            return new Dictionary<long, DateTime>();

        var ids = productIds.ToList();
        return await _db.InventoryCostTransitionBaselines
            .AsNoTracking()
            .Where(baseline => ids.Contains(baseline.ProductId))
            .ToDictionaryAsync(
                baseline => baseline.ProductId, baseline => baseline.CutoffAt, cancellationToken);
    }

    /// <inheritdoc />
    public Task SaveChangesAsync(CancellationToken cancellationToken) =>
        _db.SaveChangesAsync(cancellationToken);

    /// <summary>
    /// The business's sales that carry one of those provider product ids, have no local product, and
    /// whose stored provider id has known raw provenance (issue #758).
    ///
    /// <para>A provenance-unknown row is deliberately excluded: its stored number may be either a raw
    /// provider id or the local product id the old synchronization wrote, so reading it as a provider
    /// id could attribute a sale to a product it never belonged to - the exact error issue #758
    /// exists to end.</para>
    /// </summary>
    private IQueryable<NayaxSales> UnresolvedSales(IReadOnlyCollection<long> providerNumbers)
    {
        var numbers = providerNumbers.ToList();
        return _db.NayaxSales
            .Where(sale =>
                sale.ProductId == null
                && sale.ProviderProductIdIsRaw
                && sale.Provider == ExternalProvider.Nayax
                && sale.NayaxProductId != null
                && numbers.Contains(sale.NayaxProductId!.Value));
    }

    /// <summary>
    /// Refuses to run unless the current business's sales provider is Nayax (issue #759): this
    /// operation reads <c>Product.ExternalProductId</c> as a Nayax product id, and nothing in that
    /// column says which system issued it.
    /// </summary>
    private async Task RequireNayaxSalesProviderAsync(CancellationToken cancellationToken)
    {
        if (await EfSalesProviderQueries.CurrentBusinessSellsThroughAsync(
                _db, ExternalProvider.Nayax, cancellationToken))
            return;

        throw new InvalidOperationException(
            "Unresolved Nayax sales cannot be attributed for a business whose sales provider is not "
                + "Nayax. Nothing was changed.");
    }
}
