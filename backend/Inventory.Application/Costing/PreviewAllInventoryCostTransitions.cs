using Inventory.Application.Nayax;
using Inventory.Application.Time;
using Inventory.Domain.Exceptions;

namespace Inventory.Application.Costing;

/// <summary>
/// Previews a transition baseline for every product that does not have one yet (issue #298, child
/// 4 of #149), moved unchanged from the former <c>InventoryCostTransitionService.PreviewAllAsync</c>
/// behind <c>POST api/admin/inventory-cost-transition/preview-all</c>. Each product opens at its
/// current average unit cost, all at one cutoff; the preview is stored for 30 minutes so
/// <see cref="ApplyAllInventoryCostTransitions"/> can confirm it.
/// </summary>
public sealed class PreviewAllInventoryCostTransitions
{
    private readonly IInventoryCostTransitionStore _store;
    private readonly InventoryCostTransitionPreviewBuilder _builder;

    public PreviewAllInventoryCostTransitions(IInventoryCostTransitionStore store, INayaxLynxClient nayax, IClock clock)
    {
        _store = store;
        _builder = new InventoryCostTransitionPreviewBuilder(store, nayax, clock);
    }

    public async Task<InventoryCostTransitionBatchPreview> Handle(
        InventoryCostTransitionBatchPreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!Enum.IsDefined(request.CostSource))
            throw new DomainValidationException("Select whether the opening costs are authoritative or estimated.");
        var products = await _store.ListProductsWithoutBaselineAsync(cancellationToken);
        if (products.Count == 0)
            throw new DomainValidationException("All products already have an inventory-cost transition baseline.");
        if (products.Any(x => x.AverageUnitCost < 0))
            throw new DomainValidationException("One or more products have a negative current average unit cost.");

        var preview = await _builder.BuildBatchAsync(Guid.NewGuid(), products, request.CostSource, cancellationToken);
        _store.AddDraft(_builder.NewDraft(preview.PreviewId, 0, preview));
        await _store.SaveChangesAsync(cancellationToken);
        return preview;
    }
}
