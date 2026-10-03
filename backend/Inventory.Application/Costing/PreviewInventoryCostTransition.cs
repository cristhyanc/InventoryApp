using Inventory.Application.Nayax;
using Inventory.Application.Time;
using Inventory.Domain.Costing;
using Inventory.Domain.Exceptions;

namespace Inventory.Application.Costing;

/// <summary>
/// Previews one product's inventory-cost transition baseline (issue #298, child 4 of #149), moved
/// unchanged from the former <c>InventoryCostTransitionService.PreviewAsync</c> behind
/// <c>POST api/admin/inventory-cost-transition/preview</c>. Verified home stock plus each Nayax
/// machine's stock becomes the opening costing quantity at the requested average unit cost; the
/// preview is stored for 30 minutes so <see cref="ApplyInventoryCostTransition"/> can confirm it.
/// A product that already has a baseline is rejected.
/// </summary>
public sealed class PreviewInventoryCostTransition
{
    private readonly IInventoryCostTransitionStore _store;
    private readonly InventoryCostTransitionPreviewBuilder _builder;

    public PreviewInventoryCostTransition(IInventoryCostTransitionStore store, INayaxLynxClient nayax, IClock clock)
    {
        _store = store;
        _builder = new InventoryCostTransitionPreviewBuilder(store, nayax, clock);
    }

    public async Task<InventoryCostTransitionPreview> Handle(
        InventoryCostTransitionPreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        InventoryCostTransitionPolicy.EnsureValidOpeningCost(request.AverageUnitCost);
        if (!Enum.IsDefined(request.CostSource))
            throw new DomainValidationException("Select whether the opening cost is authoritative or estimated.");
        if (await _store.AnyBaselineAsync([request.ProductId], cancellationToken))
            throw new DomainValidationException("This product already has an inventory-cost transition baseline.");

        var preview = await BuildPreviewAsync(_store, _builder, request, Guid.NewGuid(), cancellationToken);
        _store.AddDraft(_builder.NewDraft(preview.PreviewId, preview.ProductId, preview));
        await _store.SaveChangesAsync(cancellationToken);
        return preview;
    }

    internal static async Task<InventoryCostTransitionPreview> BuildPreviewAsync(
        IInventoryCostTransitionStore store,
        InventoryCostTransitionPreviewBuilder builder,
        InventoryCostTransitionPreviewRequest request,
        Guid previewId,
        CancellationToken cancellationToken)
    {
        var product = await store.GetProductAsync(request.ProductId, cancellationToken)
            ?? throw new DomainValidationException($"Product {request.ProductId} does not exist.");
        var (_, previews) = await builder.BuildAsync(
            previewId, [product], _ => request.AverageUnitCost, request.CostSource, cancellationToken);
        return previews[0];
    }
}
