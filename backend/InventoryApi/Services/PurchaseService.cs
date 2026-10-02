using Inventory.Application.Purchases;
using Inventory.Domain.Purchases;
using InventoryApi.Adapters.Mapping;
using InventoryApi.DTOs;
using InventoryApi.Models;
using InventoryApi.Services.Interfaces;

namespace InventoryApi.Services;

/// <summary>
/// A transitional delegator only (issue #281, the same shape #240 left <see cref="ProductService"/>
/// in): every method maps the <see cref="IPurchaseService"/> request onto the migrated
/// <see cref="Inventory.Application.Purchases"/> use case that owns it, and maps the result back to
/// the unchanged <see cref="Purchase"/> API response through <see cref="PurchaseResponseMapper"/>.
/// It holds no <c>AppDbContext</c>, no query, and no rule of its own. Deleting it, and with it
/// <see cref="IPurchaseService"/>, is tracked by issue #153.
/// </summary>
public class PurchaseService : IPurchaseService
{
    private readonly ListPurchases _listPurchases;
    private readonly GetPurchase _getPurchase;
    private readonly GetPurchaseFile _getPurchaseFile;
    private readonly UploadPurchase _uploadPurchase;
    private readonly UpdatePurchase _updatePurchase;
    private readonly DeletePurchase _deletePurchase;
    private readonly ComputePurchaseTotalValidation _computeValidation;

    public PurchaseService(
        ListPurchases listPurchases,
        GetPurchase getPurchase,
        GetPurchaseFile getPurchaseFile,
        UploadPurchase uploadPurchase,
        UpdatePurchase updatePurchase,
        DeletePurchase deletePurchase,
        ComputePurchaseTotalValidation computeValidation)
    {
        _listPurchases = listPurchases;
        _getPurchase = getPurchase;
        _getPurchaseFile = getPurchaseFile;
        _uploadPurchase = uploadPurchase;
        _updatePurchase = updatePurchase;
        _deletePurchase = deletePurchase;
        _computeValidation = computeValidation;
    }

    public async Task<IEnumerable<Purchase>> GetAll(int? supplierId)
    {
        var records = await _listPurchases.Handle(supplierId, CancellationToken.None);
        return records.Select(PurchaseResponseMapper.ToPurchase).ToList();
    }

    public async Task<Purchase?> Get(int id)
    {
        var record = await _getPurchase.Handle(id, CancellationToken.None);
        return record is null ? null : PurchaseResponseMapper.ToPurchase(record);
    }

    public async Task<(byte[]? Content, string? ContentType, string? FileName)> GetFile(int id)
    {
        var file = await _getPurchaseFile.Handle(id, CancellationToken.None);
        return file is null ? (null, null, null) : (file.Content, file.ContentType, file.FileName);
    }

    public async Task<Purchase?> Upload(
        IFormFile file, string title, string? notes, decimal? totalAmount, decimal? deliveryCost, decimal? packageCost,
        DateTime? purchaseDate, int? supplierId, IReadOnlyList<PurchaseItemDto>? items = null)
    {
        if (file is null) return null;

        var fileInput = new PurchaseFileInput(file.FileName, file.ContentType, file.Length, file.OpenReadStream);
        var fields = new PurchaseFields(title, notes, totalAmount, deliveryCost, packageCost, purchaseDate, supplierId);
        var itemInputs = (items ?? Array.Empty<PurchaseItemDto>()).Select(ToItemInput).ToList();

        var record = await _uploadPurchase.Handle(fileInput, fields, itemInputs, CancellationToken.None);
        return record is null ? null : PurchaseResponseMapper.ToPurchase(record);
    }

    public async Task<Purchase?> Update(
        int id, string? title, string? notes, decimal? totalAmount, decimal? deliveryCost, decimal? packageCost,
        DateTime? purchaseDate, int? supplierId, IReadOnlyList<PurchaseItemDto>? items = null)
    {
        var fields = new PurchaseFields(title, notes, totalAmount, deliveryCost, packageCost, purchaseDate, supplierId);
        var itemInputs = items?.Select(ToItemInput).ToList();

        var record = await _updatePurchase.Handle(id, fields, itemInputs, CancellationToken.None);
        return record is null ? null : PurchaseResponseMapper.ToPurchase(record);
    }

    public Task<bool> Delete(int id) => _deletePurchase.Handle(id, CancellationToken.None);

    public PurchaseValidationDto? ComputeValidation(Purchase purchase)
    {
        var items = purchase.Items ?? new List<PurchaseItem>();
        var validationItems = items.Select(i => new PurchaseTotalValidationItem(i.Quantity, i.UnitCost));
        var result = _computeValidation.Handle(purchase.TotalAmount, purchase.DeliveryCost, purchase.PackageCost, validationItems);
        return new PurchaseValidationDto(
            result.HasMismatch,
            result.ItemSubtotal,
            result.CalculatedTotal,
            result.Difference);
    }

    private static PurchaseItemInput ToItemInput(PurchaseItemDto dto) => new(dto.ProductId, dto.Quantity, dto.UnitCost);
}
