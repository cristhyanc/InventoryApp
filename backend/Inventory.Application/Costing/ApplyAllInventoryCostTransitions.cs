using Inventory.Application.Nayax;
using Inventory.Application.Time;
using Inventory.Domain.Costing;
using Inventory.Domain.Exceptions;

namespace Inventory.Application.Costing;

/// <summary>
/// Confirms a stored all-products transition preview (issue #298, child 4 of #149), moved unchanged
/// from the former <c>InventoryCostTransitionService.ApplyAllAsync</c> behind
/// <c>POST api/admin/inventory-cost-transition/apply-all</c>. In one transaction it rejects an
/// applied or expired preview, rejects it when any previewed product has since received a baseline
/// or no longer exists, rebuilds the preview and rejects it when the eligible products, an average
/// unit cost or any inventory input changed, saves every baseline exactly as previewed, marks the
/// preview applied, and rebuilds each product's cost history from the cutoff - all or nothing.
/// </summary>
public sealed class ApplyAllInventoryCostTransitions
{
    private readonly IInventoryCostTransitionStore _store;
    private readonly IRebuildProductCost _rebuild;
    private readonly IClock _clock;
    private readonly InventoryCostTransitionPreviewBuilder _builder;

    public ApplyAllInventoryCostTransitions(
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

    public async Task<InventoryCostTransitionBatchPreview> Handle(
        ApplyInventoryCostTransitionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.Confirmed)
            throw new DomainValidationException("Explicit confirmation is required to save all transition baselines.");

        await using var transaction = await _store.BeginTransactionAsync(cancellationToken);
        var draft = await _store.FindDraftAsync(request.PreviewId, InventoryCostTransitionPreviewScope.AllProducts, cancellationToken)
            ?? throw new DomainValidationException("The all-products transition preview does not exist. Run the preview again.");
        InventoryCostTransitionPolicy.EnsureDraftUsable(draft.AppliedAt, draft.ExpiresAt, _clock.UtcNow);
        var expected = InventoryCostTransitionPreviewBuilder.ReadSnapshot<InventoryCostTransitionBatchPreview>(draft);
        var productIds = expected.Products.Select(x => x.ProductId).ToList();
        if (await _store.AnyBaselineAsync(productIds, cancellationToken))
            throw new DomainValidationException("One or more products received a baseline after this preview. Run the preview again.");
        var products = await _store.ListProductsAsync(productIds, cancellationToken);
        if (products.Count != productIds.Count)
            throw new DomainValidationException("One or more previewed products no longer exist.");
        var current = await _builder.BuildBatchAsync(Guid.NewGuid(), products, expected.CostSource, cancellationToken);
        InventoryCostTransitionPolicy.EnsureBatchUnchanged(
            expected.Products.Select(InventoryCostTransitionPreviewBuilder.ToState).ToList(),
            current.Products.Select(InventoryCostTransitionPreviewBuilder.ToState).ToList());

        foreach (var product in expected.Products)
            _store.AddBaseline(product);
        _store.MarkDraftApplied(draft.Id, _clock.UtcNow);
        await _store.SaveChangesAsync(cancellationToken);
        foreach (var product in expected.Products)
            await _rebuild.RebuildAsync(product.ProductId, product.CutoffAt, cancellationToken: cancellationToken);
        await _store.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return expected;
    }
}
