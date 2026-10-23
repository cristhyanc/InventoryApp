using Inventory.Application.Access;
using Inventory.Application.Gst;
using Inventory.Application.Suppliers;
using Inventory.Domain.Gst;
using InventoryApi.Auth;
using InventoryApi.DTOs;
using InventoryApi.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

/// <summary>
/// The suppliers a business buys from, and each supplier's explicitly configured GST defaults.
///
/// Maintaining a supplier is day-to-day purchasing work, so the controller requires
/// <c>Purchasing.Operate</c> (issue #525). <em>Setting</em> a supplier's GST defaults additionally
/// requires <c>Suppliers.ManageGstDefaults</c>, declared on that one action: a default is accounting
/// configuration that decides how later purchases are classified, which is a Manager's decision
/// rather than an Operator's. Reading the defaults stays on <c>Purchasing.Operate</c>, because an
/// Operator recording a purchase needs to see which classification it will be given.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
[RequiredScope("access_as_user")]
[RequireCapability(Capability.PurchasingOperate)]
public class SuppliersController : ControllerBase
{
    private readonly ListSuppliers _listSuppliers;
    private readonly GetSupplier _getSupplier;
    private readonly CreateSupplier _createSupplier;
    private readonly UpdateSupplier _updateSupplier;
    private readonly DeleteSupplier _deleteSupplier;
    private readonly GetSupplierGstDefaults _getSupplierGstDefaults;
    private readonly SetSupplierGstDefaults _setSupplierGstDefaults;

    public SuppliersController(
        ListSuppliers listSuppliers,
        GetSupplier getSupplier,
        CreateSupplier createSupplier,
        UpdateSupplier updateSupplier,
        DeleteSupplier deleteSupplier,
        GetSupplierGstDefaults getSupplierGstDefaults,
        SetSupplierGstDefaults setSupplierGstDefaults)
    {
        _getSupplierGstDefaults = getSupplierGstDefaults;
        _setSupplierGstDefaults = setSupplierGstDefaults;
        _listSuppliers = listSuppliers;
        _getSupplier = getSupplier;
        _createSupplier = createSupplier;
        _updateSupplier = updateSupplier;
        _deleteSupplier = deleteSupplier;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<SupplierResponse>>> GetAll(CancellationToken cancellationToken)
    {
        var suppliers = await _listSuppliers.Handle(cancellationToken);
        return Ok(suppliers.Select(ToResponse).ToList());
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<SupplierResponse>> Get(int id, CancellationToken cancellationToken)
    {
        var supplier = await _getSupplier.Handle(id, cancellationToken);
        return supplier is null ? NotFound() : Ok(ToResponse(supplier));
    }

    [HttpPost]
    public async Task<ActionResult<SupplierResponse>> Create(SupplierDto dto, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dto);

        var result = await _createSupplier.Handle(
            dto.Name, dto.ContactName, dto.Phone, dto.Email, dto.Address, dto.Url, cancellationToken);

        return result.Outcome switch
        {
            SupplierWriteOutcome.Success =>
                CreatedAtAction(nameof(Get), new { id = result.Supplier!.Id }, ToResponse(result.Supplier)),
            _ => BadRequest(UrlProblem(result.ValidationError!)),
        };
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, SupplierDto dto, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dto);

        var result = await _updateSupplier.Handle(
            id, dto.Name, dto.ContactName, dto.Phone, dto.Email, dto.Address, dto.Url, cancellationToken);

        return result.Outcome switch
        {
            SupplierWriteOutcome.Success => NoContent(),
            SupplierWriteOutcome.NotFound => NotFound(),
            _ => BadRequest(UrlProblem(result.ValidationError!)),
        };
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        var ok = await _deleteSupplier.Handle(id, cancellationToken);
        return ok ? NoContent() : NotFound();
    }

    /// <summary>
    /// The supplier's explicitly configured GST defaults (issue #430). They are their own resource
    /// rather than fields on the supplier payload because that payload is the pinned legacy
    /// <c>Supplier</c> API component every nested <c>supplier</c> object references; the reasoning
    /// is on the persisted default properties themselves and in docs/architecture.md § Product and
    /// supplier GST rules.
    /// </summary>
    [HttpGet("{id:int}/gst-defaults")]
    public async Task<ActionResult<SupplierGstDefaultsResponse>> GetGstDefaults(int id, CancellationToken cancellationToken)
    {
        var defaults = await _getSupplierGstDefaults.Handle(id, cancellationToken);
        return defaults is null
            ? NotFound()
            : Ok(new SupplierGstDefaultsResponse(
                id, defaults.Value.ProductLines, defaults.Value.Delivery, defaults.Value.Package));
    }

    /// <summary>
    /// Sets the supplier's GST defaults. Configuration only: it reclassifies no existing purchase
    /// line or charge, and a default applies only where the purchased product has no rule of its own.
    ///
    /// The one action on this controller with a capability of its own (issue #525). The
    /// controller-level <c>Purchasing.Operate</c> still applies, so this action needs both; every
    /// role that holds <c>Suppliers.ManageGstDefaults</c> holds <c>Purchasing.Operate</c> too, which
    /// makes the GST capability the effective rule and the combination strictly narrower than either.
    /// </summary>
    [HttpPut("{id:int}/gst-defaults")]
    [RequireCapability(Capability.SuppliersManageGstDefaults)]
    public async Task<IActionResult> SetGstDefaults(int id, SupplierGstDefaultsDto dto, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dto);

        var result = await _setSupplierGstDefaults.Handle(
            id,
            new SupplierGstDefaults(dto.ProductLineGstDefault, dto.DeliveryGstDefault, dto.PackageGstDefault),
            cancellationToken);

        return result.Outcome switch
        {
            GstRuleUpdateOutcome.Success => NoContent(),
            GstRuleUpdateOutcome.NotFound => NotFound(),
            _ => BadRequest(result.ValidationError),
        };
    }

    private static SupplierResponse ToResponse(SupplierRecord record) =>
        new(record.Id, record.Name, record.ContactName, record.Phone, record.Email, record.Address, record.Url);

    /// <summary>
    /// The only field a supplier write refuses today is <c>url</c> (issue #847), so the refusal is
    /// reported against that field. It is a <see cref="ValidationProblemDetails"/> - the shape
    /// <c>[ApiController]</c> already produces for a body that fails model binding - so a client
    /// puts the message back on the form field the operator typed in, rather than reading a bare
    /// string.
    /// </summary>
    private static ValidationProblemDetails UrlProblem(string message) =>
        new(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["url"] = [message] })
        {
            Title = DomainExceptionHandler.ValidationTitle,
            Status = StatusCodes.Status400BadRequest,
        };
}
