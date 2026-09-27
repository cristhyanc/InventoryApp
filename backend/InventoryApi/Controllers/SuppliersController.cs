using Inventory.Application.Suppliers;
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

    public SuppliersController(
        ListSuppliers listSuppliers,
        GetSupplier getSupplier,
        CreateSupplier createSupplier,
        UpdateSupplier updateSupplier,
        DeleteSupplier deleteSupplier)
    {
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

    private static SupplierResponse ToResponse(SupplierRecord record) =>
        new(record.Id, record.Name, record.ContactName, record.Phone, record.Email, record.Address);
}
