using Inventory.Application.Access;
using Inventory.Application.Purchases;
using Inventory.Application.SupplierOrders;
using Inventory.Domain.Gst;
using Inventory.Domain.Purchases;
using InventoryApi.Adapters.Mapping;
using InventoryApi.Auth;
using InventoryApi.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

/// <summary>
/// Recorded purchases, their stored documents and their per-component GST classification. Day-to-day
/// purchasing work, so every action requires <c>Purchasing.Operate</c> (issue #525), including the
/// document read: the receipt belongs to the purchase it was recorded with, and whoever may record
/// the purchase may see it.
///
/// <para><b>A purchase's document is optional</b> (issue #976, child of #965). <c>POST</c> accepts a
/// body with no <c>file</c> part at all, the four document values come back <c>null</c> together for
/// a purchase that has none, <c>GET .../file</c> answers <c>404</c> for one, and
/// <c>POST .../{id}/file</c> adds a document to a purchase that has none - once, never as a
/// replacement.</para>
///
/// <para><b>A line's nested catalogue product carries no <c>inventoryValue</c> for a caller without
/// <c>Dashboard.ViewFinancials</c></b> (issue #700). That nested object is the same catalogue
/// snapshot <c>api/products</c> serves, so until this slice the valuation issue #698 withheld there
/// was readable by listing purchases instead. <b>The purchase's own figures are untouched</b> - the
/// invoice total, the delivery and package charges, each line's unit cost and extended total, and
/// the input-GST block - because they are purchasing data the Operator recording the purchase
/// entered. The decision is made once per request from <see cref="CurrentCapabilities"/> and never
/// from a role name.</para>
/// </summary>
[ApiController]
[Route("api/purchases")]
[Authorize]
[RequiredScope("access_as_user")]
[RequireCapability(Capability.PurchasingOperate)]
public class PurchasesController : ControllerBase
{
    /// <summary>
    /// The message the retired <c>PurchaseService</c> delegator's <c>null</c> result stood for:
    /// an empty, oversized or wrong-typed document, or an unknown supplier. The use case still
    /// reports all of those as <c>null</c>, so the one answer stays deliberately vague about which
    /// it was, exactly as before. A <em>missing</em> document left this list in issue #976: it is
    /// now an ordinary purchase with no document, not a refusal.
    /// </summary>
    private const string InvalidFileOrSupplierMessage = "Invalid file or supplier";

    private readonly ListPurchases _listPurchases;
    private readonly GetPurchase _getPurchase;
    private readonly GetPurchaseFile _getPurchaseFile;
    private readonly UploadPurchase _uploadPurchase;
    private readonly AttachPurchaseDocument _attachPurchaseDocument;
    private readonly UpdatePurchase _updatePurchase;
    private readonly DeletePurchase _deletePurchase;
    private readonly ComputePurchaseTotalValidation _computeValidation;
    private readonly ComputePurchaseGstSummary _computeGstSummary;
    private readonly CurrentCapabilities _capabilities;

    public PurchasesController(
        ListPurchases listPurchases,
        GetPurchase getPurchase,
        GetPurchaseFile getPurchaseFile,
        UploadPurchase uploadPurchase,
        AttachPurchaseDocument attachPurchaseDocument,
        UpdatePurchase updatePurchase,
        DeletePurchase deletePurchase,
        ComputePurchaseTotalValidation computeValidation,
        ComputePurchaseGstSummary computeGstSummary,
        CurrentCapabilities capabilities)
    {
        _capabilities = capabilities;
        _listPurchases = listPurchases;
        _getPurchase = getPurchase;
        _getPurchaseFile = getPurchaseFile;
        _uploadPurchase = uploadPurchase;
        _attachPurchaseDocument = attachPurchaseDocument;
        _updatePurchase = updatePurchase;
        _deletePurchase = deletePurchase;
        _computeValidation = computeValidation;
        _computeGstSummary = computeGstSummary;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<PurchaseResponseDto>>> GetAll([FromQuery] int? supplierId)
    {
        var visibility = await _capabilities.ForCurrentRequestAsync(CancellationToken.None);
        var purchases = await _listPurchases.Handle(supplierId, CancellationToken.None);
        return Ok(purchases.Select(purchase => ToResponseDto(purchase, visibility)).ToList());
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<PurchaseResponseDto>> Get(int id)
    {
        var visibility = await _capabilities.ForCurrentRequestAsync(CancellationToken.None);
        var purchase = await _getPurchase.Handle(id, CancellationToken.None);
        if (purchase is null) return NotFound();
        return Ok(ToResponseDto(purchase, visibility));
    }

    [HttpGet("{id:int}/file")]
    public async Task<IActionResult> GetFile(int id)
    {
        var file = await _getPurchaseFile.Handle(id, CancellationToken.None);
        if (file is null) return NotFound();
        return File(file.Content, file.ContentType, file.FileName);
    }

    // multipart/form-data: file (optional, issue #976) + title + notes + totalAmount + deliveryCost
    // + deliveryGstClassification + packageCost + packageGstClassification + purchaseDate
    // + supplierId + items + completeSupplierOrderId + completeSupplierOrderFingerprint
    [HttpPost]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(10485760)]
    public async Task<ActionResult<PurchaseResponseDto>> Upload(
        IFormFile? file,
        [FromForm] string title,
        [FromForm] string? notes,
        [FromForm] decimal? totalAmount,
        [FromForm] decimal? deliveryCost,
        [FromForm] GstClassification? deliveryGstClassification,
        [FromForm] decimal? packageCost,
        [FromForm] GstClassification? packageGstClassification,
        [FromForm] DateTime? purchaseDate,
        [FromForm] int? supplierId,
        [FromForm] string? items,
        [FromForm] int? completeSupplierOrderId,
        [FromForm] string? completeSupplierOrderFingerprint)
    {
        // The uploaded file stays an HTTP concern: the controller is the only place that sees
        // IFormFile and adapts it to the Application layer's PurchaseFileInput port. A body with no
        // file part at all is a purchase with no document (issue #976), which model binding reports
        // as a null IFormFile - so "no document" and "a document the use case refuses" stay
        // different requests, answered 201 and 400 respectively.
        var visibility = await _capabilities.ForCurrentRequestAsync(CancellationToken.None);

        PurchaseRecord? purchase;
        try
        {
            purchase = await _uploadPurchase.Handle(
                ToFileInput(file),
                new PurchaseFields(
                    title, notes, totalAmount, deliveryCost, packageCost, purchaseDate, supplierId,
                    deliveryGstClassification, packageGstClassification),
                (ParseItems(items) ?? []).Select(ToItemInput).ToList(),
                ToCompletionRequest(completeSupplierOrderId, completeSupplierOrderFingerprint),
                CancellationToken.None);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
        if (purchase is null) return BadRequest(InvalidFileOrSupplierMessage);
        return CreatedAtAction(nameof(Get), new { id = purchase.Id }, ToResponseDto(purchase, visibility));
    }

    /// <summary>
    /// Adds a supporting document to a purchase that has none (issue #976). It is a write - one more
    /// action on this controller's existing <c>Purchasing.Operate</c> capability, refused in a
    /// read-only support session and in a role preview by their own central <c>GET</c>/<c>HEAD</c>
    /// rules rather than by anything written here - and it is deliberately not a replace: a purchase
    /// that already has a document is answered <c>409</c>, which is also what the loser of two
    /// concurrent attaches is told.
    /// </summary>
    // multipart/form-data: file
    [HttpPost("{id:int}/file")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(10485760)]
    public async Task<ActionResult<PurchaseResponseDto>> AddFile(int id, IFormFile? file)
    {
        if (ToFileInput(file) is not { } input)
            return BadRequest(PurchaseDocumentPolicy.MissingDocumentMessage);

        var visibility = await _capabilities.ForCurrentRequestAsync(CancellationToken.None);
        var result = await _attachPurchaseDocument.Handle(id, input, CancellationToken.None);

        return result.Outcome switch
        {
            AttachPurchaseDocumentOutcome.Attached => Ok(ToResponseDto(result.Purchase!, visibility)),
            AttachPurchaseDocumentOutcome.AlreadyHasDocument => Conflict(result.Message),
            AttachPurchaseDocumentOutcome.InvalidDocument => BadRequest(result.Message),
            _ => NotFound(),
        };
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<PurchaseResponseDto>> Update(
        int id,
        [FromForm] string? title,
        [FromForm] string? notes,
        [FromForm] decimal? totalAmount,
        [FromForm] decimal? deliveryCost,
        [FromForm] GstClassification? deliveryGstClassification,
        [FromForm] decimal? packageCost,
        [FromForm] GstClassification? packageGstClassification,
        [FromForm] DateTime? purchaseDate,
        [FromForm] int? supplierId,
        [FromForm] string? items)
    {
        var visibility = await _capabilities.ForCurrentRequestAsync(CancellationToken.None);

        PurchaseRecord? purchase;
        try
        {
            purchase = await _updatePurchase.Handle(
                id,
                new PurchaseFields(
                    title, notes, totalAmount, deliveryCost, packageCost, purchaseDate, supplierId,
                    deliveryGstClassification, packageGstClassification),
                ParseItems(items)?.Select(ToItemInput).ToList(),
                CancellationToken.None);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
        if (purchase is null) return NotFound();
        return Ok(ToResponseDto(purchase, visibility));
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
    /// Pairs the purchase with its total-validation and input-GST blocks, all three derived from the
    /// same persisted record the response carries. Neither rule lives here: the mismatch rule stays
    /// in the <see cref="ComputePurchaseTotalValidation"/> use case over
    /// <c>Inventory.Domain.Purchases.PurchaseTotalValidationPolicy</c>, and the input-GST rule in
    /// <see cref="ComputePurchaseGstSummary"/> over
    /// <c>Inventory.Domain.Purchases.PurchaseGstPolicy</c> (issue #431).
    /// </summary>
    private PurchaseResponseDto ToResponseDto(
        PurchaseRecord purchase, FinancialFigureVisibility visibility)
    {
        var result = _computeValidation.Handle(
            purchase.TotalAmount,
            purchase.DeliveryCost,
            purchase.PackageCost,
            purchase.Items.Select(item => new PurchaseTotalValidationItem(item.Quantity, item.UnitCost)));
        var gst = _computeGstSummary.Handle(purchase);

        return new PurchaseResponseDto(
            PurchaseResponseMapper.ToResponse(purchase, visibility),
            new PurchaseValidationDto(
                result.HasMismatch,
                result.ItemSubtotal,
                result.CalculatedTotal,
                result.Difference),
            new PurchaseGstSummaryDto(
                gst.InputGst,
                gst.UnresolvedComponentCount,
                gst.UnresolvedAmount));
    }

    /// <summary>
    /// Adapts the posted <see cref="IFormFile"/> to the Application layer's transport-free port, or
    /// reports no document at all when the multipart body carried no <c>file</c> part (issue #976).
    /// A part that is present but empty is a document - an empty one - and is refused by the
    /// document rules rather than treated as an absent one, so "I attached nothing" and "I attached
    /// something unusable" stay different answers.
    /// </summary>
    private static PurchaseFileInput? ToFileInput(IFormFile? file) =>
        file is null ? null : new PurchaseFileInput(file.FileName, file.ContentType, file.Length, file.OpenReadStream);

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

    private static PurchaseItemInput ToItemInput(PurchaseItemDto dto) =>
        new(dto.ProductId, dto.Quantity, dto.UnitCost, dto.GstClassification, dto.Id);

    /// <summary>
    /// The operator's "complete this order" choice, when the form carried one (issue #974). Both
    /// fields are required together: an order id with no fingerprint is a decision nothing was shown
    /// for, so it is left out rather than sent on as a completion the save would have to guess at -
    /// and the save then refuses it, because an empty fingerprint matches nothing it recomputes.
    /// Nothing else about the order travels inwards.
    /// </summary>
    private static SupplierOrderCompletionRequest? ToCompletionRequest(int? supplierOrderId, string? fingerprint) =>
        supplierOrderId is { } id && !string.IsNullOrWhiteSpace(fingerprint)
            ? new SupplierOrderCompletionRequest(id, fingerprint)
            : null;
}
