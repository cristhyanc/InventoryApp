using Inventory.Application.Costing;
using Inventory.Application.Tenancy;
using Inventory.Application.Time;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Tenancy;

namespace Inventory.Application.CatalogueReview;

/// <summary>One product's resolved sales, as the operation applied them.</summary>
public sealed record LinkedProviderSalesResolved(
    long ProductId,
    string ProductName,
    string ProviderProductId,
    int ResolvedSaleCount,
    bool CostingReplayed,
    int RecostedSaleCount);

/// <summary>What one run of <see cref="ResolveLinkedProviderSales"/> did.</summary>
public sealed record LinkedProviderSaleResolution(
    DateTime ResolvedAtUtc,
    IReadOnlyList<LinkedProviderSalesResolved> Products)
{
    /// <summary>How many stored sales were given their local product.</summary>
    public int ResolvedSaleCount => Products.Sum(product => product.ResolvedSaleCount);

    /// <summary>How many products' costing was replayed.</summary>
    public int ProductsReplayed => Products.Count(product => product.CostingReplayed);
}

/// <summary>
/// Gives the sales a catalogue link made resolvable their local product, and replays the affected
/// products' costing - as one explicit, audited, atomic operation (issue #738, parent #329 decision
/// C1).
///
/// <para><strong>Why it is separate from the catalogue apply.</strong> Linking a provider product id
/// to a local product says what the id means; it does not say that the business's historical sales
/// carrying that id belong to that product's inventory. Attributing them moves product-level revenue,
/// consumes costed inventory and recosts the sales after them, which is a financial decision an
/// operator takes deliberately rather than a side effect of tidying a catalogue. So the catalogue
/// review only ever <em>reports</em> how many such sales there are, and this operation is what
/// resolves them.</para>
///
/// <para><strong>It only ever fills a null.</strong> A sale that already has a product is not in the
/// unresolved list, and the write is guarded on the column still being null, so a rerun writes
/// nothing, changes nothing and reports nothing resolved. That is what makes it idempotent, and it is
/// also what makes it safe: nothing here can move a sale off the product it is already attributed
/// to.</para>
///
/// <para><strong>The provider id is the match, never the name.</strong> A sale is resolved because the
/// provider id it recorded is the one the product now carries, and only while that recorded id has
/// known raw provenance (<c>NayaxSales.ProviderProductIdIsRaw</c>, issue #758) - a provenance-unknown
/// value may be either a provider id or the local product id the old synchronization wrote, so it is
/// read as an identifier of nothing.</para>
///
/// <para><strong>Failure semantics are all-or-nothing.</strong> A product whose cost history cannot be
/// replayed aborts the whole run: the transaction rolls back, no sale keeps its new product, no audit
/// row survives, and the operator is told which product has to be fixed first. Nothing fabricates an
/// opening cost to make the replay succeed, and a failed replay is never reported as a successful
/// resolution (AGENTS.md § Inventory and historical costing invariants).</para>
/// </summary>
public sealed class ResolveLinkedProviderSales
{
    private readonly ILinkedProviderSaleResolutionStore _store;
    private readonly IRebuildProductCost _rebuild;
    private readonly IAuthenticatedActorAccessor _actors;
    private readonly IClock _clock;

    public ResolveLinkedProviderSales(
        ILinkedProviderSaleResolutionStore store,
        IRebuildProductCost rebuild,
        IAuthenticatedActorAccessor actors,
        IClock clock)
    {
        _store = store;
        _rebuild = rebuild;
        _actors = actors;
        _clock = clock;
    }

    public async Task<LinkedProviderSaleResolution> Handle(
        bool confirmed, CancellationToken cancellationToken = default)
    {
        if (!confirmed)
            throw new DomainValidationException(
                "Explicit confirmation is required to attribute unresolved sales to the products the "
                    + "catalogue review linked, because it consumes costed inventory and replays those "
                    + "products' cost history. Nothing was changed.");

        // Resolved before anything is read or written: a resolution that cannot name who applied it
        // is not auditable, so an unidentifiable caller is refused rather than recorded as blank.
        var actor = _actors.GetCurrentActor();
        var identity = actor.Actor
            ?? throw new BusinessAccessDeniedException(
                actor.DenialReason ?? BusinessAccessDenialReason.UnidentifiableActor);

        var resolvedAt = _clock.UtcNow;

        await using var transaction = await _store.BeginTransactionAsync(cancellationToken);
        var groups = await _store.ListAsync(cancellationToken);
        if (groups.Count == 0)
            return new LinkedProviderSaleResolution(resolvedAt, []);

        var resolvedCounts = await _store.ResolveAsync(
            groups, identity.ToString(), resolvedAt, cancellationToken);

        // Saved before the replay, so the ledger read inside this same transaction sees the audit
        // rows beside the sales it is about to cost. Nothing is visible outside the transaction until
        // the commit below.
        await _store.SaveChangesAsync(cancellationToken);

        var replayable = groups
            .Where(group => Resolved(resolvedCounts, group) > 0 && group.EarliestCompletedSaleUtc.HasValue)
            .ToList();
        var cutoffs = await _store.GetTransitionCutoffsAsync(
            replayable.Select(group => group.ProductId).ToList(), cancellationToken);

        var results = new List<LinkedProviderSalesResolved>();
        foreach (var group in groups)
        {
            var from = group.EarliestCompletedSaleUtc;

            // The same gate the uploaded sales import and the latest-sales synchronization apply: a
            // sale at or before its product's transition baseline cutoff is covered by that baseline,
            // so replaying it would recost history the transition owns, and a product with no
            // baseline is replayed by no write path in the application - this one does not invent one.
            var planned = Resolved(resolvedCounts, group) > 0
                && from.HasValue
                && cutoffs.TryGetValue(group.ProductId, out var cutoff)
                && from.Value > cutoff;

            results.Add(new LinkedProviderSalesResolved(
                group.ProductId,
                group.ProductName,
                group.ProviderProductId,
                Resolved(resolvedCounts, group),
                planned,
                planned ? await RebuildAsync(group, from!.Value, cancellationToken) : 0));
        }

        await _store.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new LinkedProviderSaleResolution(resolvedAt, results);
    }

    private async Task<int> RebuildAsync(
        UnresolvedLinkedSaleGroup group, DateTime from, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _rebuild.RebuildAsync(
                group.ProductId, from, cancellationToken: cancellationToken);
            return result.RecostedSaleCount;
        }
        catch (InventoryCostDataQualityException exception)
        {
            // All-or-nothing: the transaction's disposal rolls back every resolved sale and audit
            // row, and the message says which product's history has to be fixed first.
            throw new DomainValidationException(
                $"Nothing was resolved: the inventory cost of {Describe(group)} could not be replayed "
                    + $"from {from:yyyy-MM-ddTHH:mm:ssZ}. {exception.Message}");
        }
    }

    /// <summary>
    /// How many of that product's sales the store settled. Absent means none: a sale another writer
    /// settled between the read and the guarded write is simply not in the answer.
    /// </summary>
    private static int Resolved(
        IReadOnlyDictionary<long, int> resolved, UnresolvedLinkedSaleGroup group) =>
        resolved.TryGetValue(group.ProductId, out var count) ? count : 0;

    private static string Describe(UnresolvedLinkedSaleGroup group) =>
        string.IsNullOrWhiteSpace(group.ProductName)
            ? $"product {group.ProductId}"
            : $"{group.ProductName} (product {group.ProductId})";
}
