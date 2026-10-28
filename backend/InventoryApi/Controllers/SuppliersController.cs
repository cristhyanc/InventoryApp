using Inventory.Application.Access;
using Inventory.Application.Gst;
using Inventory.Application.Suppliers;
using Inventory.Domain.Gst;
using Inventory.Domain.Suppliers;
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
///
/// Issue #934 added the "Not used" switch on the same controller capability, which is the
/// <em>existing supplier-write capability</em> the owner's 27 October 2026 decision names: hiding a
/// supplier is a purchasing decision about which suppliers this business buys from, not accounting
/// configuration like the GST defaults, and no new capability is declared for it. In a read-only
/// support session it is refused like every other non-GET request, by the method rule in
/// <c>SupportSessionAccessPolicy</c> and not by anything written here.
///
/// Issue #935 added the "Available to add" list and the one-click add on that same capability, for
/// the same reason: deciding that this business buys from a supplier the platform suggests is a
/// purchasing decision. The list is a <c>GET</c> a support session may make; the add is refused
/// there, and in a role preview, by those features' own method rules.
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
    private readonly SetSupplierNotUsed _setSupplierNotUsed;
    private readonly ListAvailableSupplierPresets _listAvailableSupplierPresets;
    private readonly AddSupplierFromPreset _addSupplierFromPreset;
    private readonly GetSupplierGstDefaults _getSupplierGstDefaults;
    private readonly SetSupplierGstDefaults _setSupplierGstDefaults;

    public SuppliersController(
        ListSuppliers listSuppliers,
        GetSupplier getSupplier,
        CreateSupplier createSupplier,
        UpdateSupplier updateSupplier,
        DeleteSupplier deleteSupplier,
        SetSupplierNotUsed setSupplierNotUsed,
        ListAvailableSupplierPresets listAvailableSupplierPresets,
        AddSupplierFromPreset addSupplierFromPreset,
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
        _setSupplierNotUsed = setSupplierNotUsed;
        _listAvailableSupplierPresets = listAvailableSupplierPresets;
        _addSupplierFromPreset = addSupplierFromPreset;
    }

    /// <summary>
    /// The business's suppliers, ordered by name.
    ///
    /// <para><c>includeHidden</c> (issue #934) decides whether the suppliers marked "Not used" are
    /// among them, and defaults to <see langword="false"/>. The default is what every picker - a
    /// purchase, a supplier order, a product's order dialog - gets without asking, so a supplier
    /// taken out of use cannot reappear on a form because a client forgot a parameter. Only the
    /// Suppliers page passes <c>true</c>, because switching "Not used" back off requires seeing the
    /// row. It is an ordinary filter and chooses nothing about ownership: the business is still the
    /// caller's own, resolved from their membership.</para>
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<SupplierDetailResponse>>> GetAll(
        CancellationToken cancellationToken, [FromQuery] bool includeHidden = false)
    {
        var suppliers = await _listSuppliers.Handle(includeHidden, cancellationToken);
        return Ok(suppliers.Select(ToResponse).ToList());
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<SupplierDetailResponse>> Get(int id, CancellationToken cancellationToken)
    {
        var supplier = await _getSupplier.Handle(id, cancellationToken);
        return supplier is null ? NotFound() : Ok(ToResponse(supplier));
    }

    [HttpPost]
    public async Task<ActionResult<SupplierDetailResponse>> Create(SupplierDto dto, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dto);

        var result = await _createSupplier.Handle(
            new SupplierFields(dto.Name, dto.ContactName, dto.Phone, dto.Email, dto.Address, dto.Url),
            cancellationToken);

        return result.Outcome switch
        {
            SupplierWriteOutcome.Success =>
                CreatedAtAction(nameof(Get), new { id = result.Supplier!.Id }, ToResponse(result.Supplier)),
            _ => BadRequest(FieldProblem(result.ValidationError!)),
        };
    }

    /// <summary>
    /// Replaces the supplier's details.
    ///
    /// <para>A supplier copied from the platform's supplier master refuses a change to its name or
    /// its website with <c>400</c> and that rule's own sentence, reported against the field it is
    /// about (issue #934); the contact fields still save, and nothing at all is written when the
    /// update is refused. A supplier the business created itself is unaffected.</para>
    /// </summary>
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, SupplierDto dto, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dto);

        var result = await _updateSupplier.Handle(
            id,
            new SupplierFields(dto.Name, dto.ContactName, dto.Phone, dto.Email, dto.Address, dto.Url),
            cancellationToken);

        return result.Outcome switch
        {
            SupplierWriteOutcome.Success => NoContent(),
            SupplierWriteOutcome.NotFound => NotFound(),
            _ => BadRequest(FieldProblem(result.ValidationError!)),
        };
    }

    /// <summary>
    /// Deletes a supplier the business created itself. A copy of a platform supplier preset is
    /// refused with <c>400</c> and the rule's sentence (issue #934), which names "Not used" as what
    /// to do instead.
    /// </summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        var result = await _deleteSupplier.Handle(id, cancellationToken);

        return result.Outcome switch
        {
            SupplierWriteOutcome.Success => NoContent(),
            SupplierWriteOutcome.NotFound => NotFound(),
            _ => BadRequest(RefusalProblem(result.ValidationError!)),
        };
    }

    /// <summary>
    /// Sets or clears a linked supplier's "Not used" switch (issue #934): hidden, it is left out of
    /// the supplier pickers, and switching it back on brings it straight back. Nothing is removed
    /// either way, and a purchase, supplier order or expense that already names the supplier still
    /// resolves and displays it.
    ///
    /// <para>Its own resource rather than a field on the update body, so correcting a phone number
    /// on a stale form cannot put a supplier back into the pickers as a side effect. A supplier the
    /// business created itself is refused with <c>400</c>: it can simply be deleted.</para>
    /// </summary>
    [HttpPut("{id:int}/not-used")]
    public async Task<IActionResult> SetNotUsed(
        int id, SupplierNotUsedDto dto, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dto);

        var result = await _setSupplierNotUsed.Handle(id, dto.IsHidden, cancellationToken);

        return result.Outcome switch
        {
            SupplierWriteOutcome.Success => NoContent(),
            SupplierWriteOutcome.NotFound => NotFound(),
            _ => BadRequest(RefusalProblem(result.ValidationError!)),
        };
    }

    /// <summary>
    /// The platform supplier records this business may add, for the Suppliers page's "Available to
    /// add" section (issue #935): the enabled records that serve the business's own state - or the
    /// nationwide ones alone, for a business with no recorded state - minus the ones it already
    /// holds a copy of.
    ///
    /// <para>It takes no parameter at all: no filter, no search, no page, and no business, which is
    /// the caller's own resolved from their membership. Each record carries its id, its name and its
    /// website and nothing else; this is the one member-facing read of a platform table besides
    /// issue #873's single boolean, and it is bounded to those three fields on purpose
    /// (docs/architecture.md § Tenant ownership). A read-only support session may make it, like any
    /// other <c>GET</c> this controller's capability allows.</para>
    /// </summary>
    [HttpGet("available-presets")]
    public async Task<ActionResult<IEnumerable<AvailableSupplierPresetResponse>>> GetAvailablePresets(
        CancellationToken cancellationToken)
    {
        var available = await _listAvailableSupplierPresets.Handle(cancellationToken);

        return Ok(available
            .Select(preset => new AvailableSupplierPresetResponse(preset.Id, preset.Name, preset.Url))
            .ToList());
    }

    /// <summary>
    /// Adds one of those records to this business as a linked copy of it (issue #935).
    ///
    /// <para>The request carries the platform record's id in the route and no body: the name, the
    /// website and the link are the record's, so there is nothing a caller submits that decides what
    /// is stored, and the business is the caller's own. It is an ordinary supplier write under this
    /// controller's existing <c>Purchasing.Operate</c> capability - the same one the "Not used"
    /// switch of issue #934 uses - and it is refused in a read-only support session and in a role
    /// preview by those features' own method rules, not by anything written here.</para>
    ///
    /// <para>A record that does not exist, is no longer suggested, or does not serve this business's
    /// state is <c>404</c>: all three are one answer, so a member learns nothing about the platform's
    /// list beyond what the list above told them. A record this business already holds a copy of is
    /// <c>409</c> with that rule's own sentence - the answer to a double-click, a second tab, or a
    /// state change that committed in between - and there is still exactly one copy.</para>
    /// </summary>
    [HttpPost("available-presets/{presetId:int}")]
    public async Task<ActionResult<SupplierDetailResponse>> AddFromPreset(
        int presetId, CancellationToken cancellationToken)
    {
        var result = await _addSupplierFromPreset.Handle(presetId, cancellationToken);

        return result.Outcome switch
        {
            SupplierPresetAdditionOutcome.Added =>
                CreatedAtAction(nameof(Get), new { id = result.Supplier!.Id }, ToResponse(result.Supplier)),
            SupplierPresetAdditionOutcome.AlreadyAdded =>
                Conflict(ConflictProblem(SupplierPresetMarketPolicy.AlreadyAddedMessage)),
            _ => NotFound(),
        };
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

    /// <summary>
    /// The suppliers endpoints' own representation. The four link fields are
    /// <see cref="SupplierPresetLinkPolicy"/>'s answers, asked here once so the response and the
    /// write paths cannot disagree about what this row allows.
    /// </summary>
    private static SupplierDetailResponse ToResponse(SupplierRecord record)
    {
        var isLinked = SupplierPresetLinkPolicy.IsLinked(record.SupplierPresetId);

        return new SupplierDetailResponse(
            record.Id,
            record.Name,
            record.ContactName,
            record.Phone,
            record.Email,
            record.Address,
            record.Url,
            isLinked,
            record.IsHidden,
            SupplierPresetLinkPolicy.CanRename(isLinked),
            SupplierPresetLinkPolicy.CanDelete(isLinked));
    }

    /// <summary>
    /// A refusal of a submitted field, reported against that field. It is a
    /// <see cref="ValidationProblemDetails"/> - the shape <c>[ApiController]</c> already produces
    /// for a body that fails model binding - so a client puts the message back on the form field
    /// the operator typed in, rather than reading a bare string.
    ///
    /// <para>Two fields can be refused: <c>url</c>, by the website rule of issue #847, and
    /// <c>name</c>, by a linked copy's rename refusal of issue #934. Which one is decided by
    /// comparing the message with the one policy constant that produces it, so there is still
    /// exactly one place each sentence is written.</para>
    /// </summary>
    private static ValidationProblemDetails FieldProblem(string message)
    {
        var field = string.Equals(message, SupplierPresetLinkPolicy.RenameRefusedMessage, StringComparison.Ordinal)
            ? "name"
            : "url";

        return new ValidationProblemDetails(
            new Dictionary<string, string[]>(StringComparer.Ordinal) { [field] = [message] })
        {
            Title = DomainExceptionHandler.ValidationTitle,
            Status = StatusCodes.Status400BadRequest,
        };
    }

    /// <summary>
    /// A refusal of the request itself rather than of a field the caller filled in: a delete or a
    /// "Not used" switch this supplier does not allow (issue #934). It carries the sentence in
    /// <c>detail</c> and in the <c>message</c> extension, the same shape
    /// <see cref="DomainExceptionHandler"/> produces for a domain validation failure, so the
    /// existing client error handling reads it without a second convention.
    /// </summary>
    private static ProblemDetails RefusalProblem(string message)
    {
        var problem = new ProblemDetails
        {
            Title = DomainExceptionHandler.ValidationTitle,
            Detail = message,
            Status = StatusCodes.Status400BadRequest,
        };
        problem.Extensions["message"] = message;
        return problem;
    }

    /// <summary>
    /// A refusal because the state the caller asked for is already the case: this business holds a
    /// copy of that platform record (issue #935). Same shape as <see cref="RefusalProblem"/> with
    /// the status and title <see cref="DomainExceptionHandler"/> gives a domain conflict, so the
    /// existing client error handling reads it without a second convention.
    /// </summary>
    private static ProblemDetails ConflictProblem(string message)
    {
        var problem = new ProblemDetails
        {
            Title = DomainExceptionHandler.ConflictTitle,
            Detail = message,
            Status = StatusCodes.Status409Conflict,
        };
        problem.Extensions["message"] = message;
        return problem;
    }
}
