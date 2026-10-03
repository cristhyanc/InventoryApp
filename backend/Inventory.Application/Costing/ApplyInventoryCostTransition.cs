using Inventory.Application.Nayax;
using Inventory.Application.Time;
using Inventory.Domain.Costing;
using Inventory.Domain.Exceptions;

namespace Inventory.Application.Costing;

/// <summary>
/// Confirms one product's stored transition preview (issue #298, child 4 of #149), moved unchanged
/// from the former <c>InventoryCostTransitionService.ApplyAsync</c> behind
/// <c>POST api/admin/inventory-cost-transition/apply</c>. In one transaction it rejects an applied
/// or expired preview, rebuilds the preview from current home stock, legacy replay and Nayax machine
/// stock and rejects it when anything changed, saves the baseline exactly as previewed, marks the
/// preview applied, and rebuilds the product's cost history from the cutoff so later sales are
/// costed from the new baseline.
/// </summary>
public sealed class ApplyInventoryCostTransition
{
    private readonly IInventoryCostTransitionStore _store;
    private readonly IRebuildProductCost _rebuild;
    private readonly IClock _clock;
    private readonly InventoryCostTransitionPreviewBuilder _builder;

    public ApplyInventoryCostTransition(
        IInventoryCostTransitionStore store,
        INayaxLynxClient nayax,
        IRebuildProductCost rebuild,
        IClock clock)
    {
        _store = store;
        _rebuild = rebuild;
        _clock = clock;
        _builder = new InventoryCostTransitionPreviewBuilder(store, nayax, clock);
    }

    public async Task<InventoryCostTransitionPreview> Handle(
        ApplyInventoryCostTransitionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.Confirmed)
            throw new DomainValidationException("Explicit confirmation is required to save the transition baseline.");

        await using var transaction = await _store.BeginTransactionAsync(cancellationToken);
        var draft = await _store.FindDraftAsync(request.PreviewId, InventoryCostTransitionPreviewScope.SingleProduct, cancellationToken)
            ?? throw new DomainValidationException("The transition preview does not exist. Run the preview again.");
        InventoryCostTransitionPolicy.EnsureDraftUsable(draft.AppliedAt, draft.ExpiresAt, _clock.UtcNow);
        var expected = InventoryCostTransitionPreviewBuilder.ReadSnapshot<InventoryCostTransitionPreview>(draft);
        var current = await PreviewInventoryCostTransition.BuildPreviewAsync(
            _store,
            _builder,
            new(expected.ProductId, expected.AverageUnitCost, expected.CostSource),
            Guid.NewGuid(),
            cancellationToken);
        InventoryCostTransitionPolicy.EnsureUnchanged(
            InventoryCostTransitionPreviewBuilder.ToState(expected),
            InventoryCostTransitionPreviewBuilder.ToState(current));

        _store.AddBaseline(expected);
        _store.MarkDraftApplied(draft.Id, _clock.UtcNow);
        await _store.SaveChangesAsync(cancellationToken);
        await _rebuild.RebuildAsync(expected.ProductId, expected.CutoffAt, cancellationToken: cancellationToken);
        await _store.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return expected;
    }
}
