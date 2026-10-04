using Inventory.Application.Purchases;
using Inventory.Domain.Purchases;
using InventoryApi.Adapters.Mapping;
using InventoryApi.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

[ApiController]
[Route("api/purchases")]
[Authorize]
[RequiredScope("access_as_user")]
public class PurchasesController : ControllerBase
{
    /// <summary>
    /// The message the retired <c>PurchaseService</c> delegator's <c>null</c> result stood for:
    /// a missing, empty, oversized or wrong-typed document, or an unknown supplier. The use case
    /// still reports all of those as <c>null</c>, so the one answer stays deliberately vague about
    /// which it was, exactly as before.
    /// </summary>
    private const string InvalidFileOrSupplierMessage = "Invalid file or supplier";

    private readonly ListPurchases _listPurchases;
    private readonly GetPurchase _getPurchase;
    private readonly GetPurchaseFile _getPurchaseFile;
    private readonly UploadPurchase _uploadPurchase;
    private readonly UpdatePurchase _updatePurchase;
    private readonly DeletePurchase _deletePurchase;
    private readonly ComputePurchaseTotalValidation _computeValidation;

    public PurchasesController(
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

    [HttpGet]
    public async Task<ActionResult<IEnumerable<PurchaseResponseDto>>> GetAll([FromQuery] int? supplierId)
    {
        var purchases = await _listPurchases.Handle(supplierId, CancellationToken.None);
        return Ok(purchases.Select(ToResponseDto).ToList());
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<PurchaseResponseDto>> Get(int id)
    {
        var purchase = await _getPurchase.Handle(id, CancellationToken.None);
        if (purchase is null) return NotFound();
        return Ok(ToResponseDto(purchase));
    }

    [HttpGet("{id:int}/file")]
    public async Task<IActionResult> GetFile(int id)
    {
        var file = await _getPurchaseFile.Handle(id, CancellationToken.None);
        if (file is null) return NotFound();
        return File(file.Content, file.ContentType, file.FileName);
    }

    // multipart/form-data: file + title + notes + totalAmount + deliveryCost + packageCost + purchaseDate + supplierId
    [HttpPost]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(10485760)]
    public async Task<ActionResult<PurchaseResponseDto>> Upload(
        IFormFile file,
        [FromForm] string title,
        [FromForm] string? notes,
        [FromForm] decimal? totalAmount,
        [FromForm] decimal? deliveryCost,
        [FromForm] decimal? packageCost,
        [FromForm] DateTime? purchaseDate,
        [FromForm] int? supplierId,
        [FromForm] string? items)
    {
        // The uploaded file stays an HTTP concern: the controller is the only place that sees
        // IFormFile and adapts it to the Application layer's PurchaseFileInput port.
        if (file is null) return BadRequest(InvalidFileOrSupplierMessage);

        PurchaseRecord? purchase;
        try
        {
            purchase = await _uploadPurchase.Handle(
                new PurchaseFileInput(file.FileName, file.ContentType, file.Length, file.OpenReadStream),
                new PurchaseFields(title, notes, totalAmount, deliveryCost, packageCost, purchaseDate, supplierId),
                (ParseItems(items) ?? []).Select(ToItemInput).ToList(),
                CancellationToken.None);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
        if (purchase is null) return BadRequest(InvalidFileOrSupplierMessage);
        return CreatedAtAction(nameof(Get), new { id = purchase.Id }, ToResponseDto(purchase));
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<PurchaseResponseDto>> Update(
        int id,
        [FromForm] string? title,
        [FromForm] string? notes,
        [FromForm] decimal? totalAmount,
        [FromForm] decimal? deliveryCost,
        [FromForm] decimal? packageCost,
        [FromForm] DateTime? purchaseDate,
        [FromForm] int? supplierId,
        [FromForm] string? items)
    {
        PurchaseRecord? purchase;
        try
        {
            purchase = await _updatePurchase.Handle(
                id,
                new PurchaseFields(title, notes, totalAmount, deliveryCost, packageCost, purchaseDate, supplierId),
                ParseItems(items)?.Select(ToItemInput).ToList(),
                CancellationToken.None);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
        if (purchase is null) return NotFound();
        return Ok(ToResponseDto(purchase));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        try
        {
            var ok = await _deletePurchase.Handle(id, CancellationToken.None);
            return ok ? NoContent() : NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Pairs the purchase with its total-validation block, both derived from the same persisted
    /// record the response carries. The mismatch rule itself stays in the one authoritative
    /// <see cref="ComputePurchaseTotalValidation"/> use case over
    /// <c>Inventory.Domain.Purchases.PurchaseTotalValidationPolicy</c>.
    /// </summary>
    private PurchaseResponseDto ToResponseDto(PurchaseRecord purchase)
    {
        var result = _computeValidation.Handle(
            purchase.TotalAmount,
            purchase.DeliveryCost,
            purchase.PackageCost,
            purchase.Items.Select(item => new PurchaseTotalValidationItem(item.Quantity, item.UnitCost)));

        return new PurchaseResponseDto(
            PurchaseResponseMapper.ToResponse(purchase),
            new PurchaseValidationDto(
                result.HasMismatch,
                result.ItemSubtotal,
                result.CalculatedTotal,
                result.Difference));
    }

    /// <summary>
    /// The posted items are a JSON string inside a multipart form, so parsing them is transport
    /// work the controller keeps. A missing or blank field is an empty item list, not a null one -
    /// the distinction matters on <c>PUT</c>, where only a literal <c>null</c> body leaves the
    /// stored items untouched - and both cases behave exactly as they did through the retired
    /// delegator.
    /// </summary>
    private static IReadOnlyList<PurchaseItemDto>? ParseItems(string? items) =>
        string.IsNullOrWhiteSpace(items) ? Array.Empty<PurchaseItemDto>() :
        System.Text.Json.JsonSerializer.Deserialize<IReadOnlyList<PurchaseItemDto>>(
            items,
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

    private static PurchaseItemInput ToItemInput(PurchaseItemDto dto) => new(dto.ProductId, dto.Quantity, dto.UnitCost);
}
