using Inventory.Application.Gst;
using Inventory.Application.Suppliers;
using Inventory.Domain.Gst;
using InventoryApi.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
[RequiredScope("access_as_user")]
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
        var supplier = await _createSupplier.Handle(dto.Name, dto.ContactName, dto.Phone, dto.Email, dto.Address, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = supplier.Id }, ToResponse(supplier));
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, SupplierDto dto, CancellationToken cancellationToken)
    {
        var ok = await _updateSupplier.Handle(id, dto.Name, dto.ContactName, dto.Phone, dto.Email, dto.Address, cancellationToken);
        return ok ? NoContent() : NotFound();
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
    /// </summary>
    [HttpPut("{id:int}/gst-defaults")]
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
        new(record.Id, record.Name, record.ContactName, record.Phone, record.Email, record.Address);
}
