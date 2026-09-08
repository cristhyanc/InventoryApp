using InventoryApi.DTOs;

namespace InventoryApi.Services.Interfaces;

public interface IInventoryCostTransitionService
{
    Task<InventoryCostTransitionPreview> PreviewAsync(
        InventoryCostTransitionPreviewRequest request,
        CancellationToken cancellationToken = default);

    Task<InventoryCostTransitionPreview> ApplyAsync(
        ApplyInventoryCostTransitionRequest request,
        CancellationToken cancellationToken = default);

    Task<InventoryCostTransitionBatchPreview> PreviewAllAsync(
        InventoryCostTransitionBatchPreviewRequest request,
        CancellationToken cancellationToken = default);

    Task<InventoryCostTransitionBatchPreview> ApplyAllAsync(
        ApplyInventoryCostTransitionRequest request,
        CancellationToken cancellationToken = default);
}
