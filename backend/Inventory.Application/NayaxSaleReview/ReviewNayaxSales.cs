using Inventory.Application.SalesSync;
using Inventory.Application.Tenancy;
using Inventory.Domain.Tenancy;

namespace Inventory.Application.NayaxSaleReview;

/// <summary>
/// The Nayax sales review use cases (issue #1105): the list and Dashboard summary every member with
/// <c>Dashboard.View</c> may read, and the business Owner's decision.
///
/// <para>A decision commits first and its inventory-cost rebuild drains afterwards, through the same
/// pending-rebuild path and data-quality failure handling as the sales sync
/// (<see cref="ILatestNayaxSalesStore.RebuildInventoryCostsAsync"/>). A failed rebuild leaves its
/// pending row for the next drain and never undoes the decision.</para>
/// </summary>
public sealed class ReviewNayaxSales
{
    private readonly INayaxSaleReviewStore _store;
    private readonly ILatestNayaxSalesStore _sales;
    private readonly IAuthenticatedActorAccessor _actors;

    public ReviewNayaxSales(
        INayaxSaleReviewStore store, ILatestNayaxSalesStore sales, IAuthenticatedActorAccessor actors)
    {
        _store = store;
        _sales = sales;
        _actors = actors;
    }

    public Task<NayaxSaleReviewPage> ListAsync(NayaxSaleReviewQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return _store.ListAsync(query, cancellationToken);
    }

    public Task<NayaxSaleReviewSummary> SummaryAsync(CancellationToken cancellationToken) =>
        _store.SummaryAsync(cancellationToken);

    /// <summary>
    /// Applies the Owner's decision, then drains the pending rebuilds it recorded.
    /// <see langword="null"/> when this business has no such queue sale.
    /// </summary>
    public async Task<NayaxSaleDecisionApplied?> DecideAsync(
        NayaxSaleDecisionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Before anything is written, so a decision can never be committed for a request whose actor
        // the audit could not name.
        var actor = _actors.GetCurrentActor().Actor
            ?? throw new BusinessAccessDeniedException(BusinessAccessDenialReason.NotAuthenticated);

        var applied = await _store
            .DecideAsync(request, new NayaxSaleDecisionActor(actor.DirectoryTenantId, actor.ObjectId), cancellationToken)
            .ConfigureAwait(false);

        if (applied is not null)
        {
            // After commit, never inside it: the decision stands whatever the replay finds. A product
            // whose history cannot be replayed keeps its pending row, which the next sync retries.
            await _sales.RebuildInventoryCostsAsync(cancellationToken).ConfigureAwait(false);
        }

        return applied;
    }
}
