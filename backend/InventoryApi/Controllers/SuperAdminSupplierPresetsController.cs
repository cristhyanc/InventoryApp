using Inventory.Application.PlatformAdmin;
using InventoryApi.Auth.PlatformAdmin;
using InventoryApi.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

/// <summary>
/// The Super Admin's platform supplier presets (issue #848, child B of #835): the one platform-wide
/// list of suggested suppliers, which a Super Admin may list, add to, edit, and enable or disable.
///
/// <para><strong>Authorisation is the other two Super Admin surfaces', unchanged.</strong>
/// <c>[Authorize(Policy = PlatformAdminPolicy.Name)]</c> is evaluated by the authorization
/// middleware before any action here runs, and <c>BusinessScopeMiddleware</c> re-evaluates the same
/// policy before it allows the request past the membership requirement. That is class 2 of the four
/// ways an endpoint may declare its authorization (<c>EndpointAuthorizationClassificationTests</c>),
/// and both halves are required: the marker alone grants nothing, and the policy alone would not be
/// eligible for the membership bypass. There is no token claim, header, route value or body field
/// that selects the authority: it is the configured Entra <c>(tid, oid)</c> pair and nothing else,
/// and both values empty - the shipped state - means nobody can reach this controller at all. An
/// Owner of a business is refused here exactly as any other member is, because a business role is
/// not platform authority. A role preview is refused on the two writes by
/// <c>RolePreviewMiddleware</c>, like every other write in the application.</para>
///
/// <para><strong>No business is involved, in either direction.</strong> Unlike the businesses
/// surface beside it, nothing here names a business at all: a preset belongs to none, carries no
/// business key, and is never copied into one by this controller - that is issue #835 child C, and
/// it is explicitly out of scope here. The request carries a <em>denied</em> <c>BusinessScope</c>, so
/// the tenant query filters and <c>BusinessOwnershipEnforcer</c> stay fully in force and nothing
/// tenant-owned - a business's own <c>Suppliers</c> rows in particular - is readable or writable
/// through it. See <c>Inventory.Infrastructure.Persistence.EfSupplierPresetStore</c> and
/// docs/architecture.md § Tenant ownership.</para>
///
/// <para><strong>There is no delete.</strong> A preset that should stop being suggested is disabled
/// through the <c>PUT</c>, which is reversible and leaves whatever a business already copied from it
/// untouched.</para>
///
/// <para>The controller is thin, as every controller here is: it binds the route value and the body,
/// invokes a use case with the request's cancellation token, and maps the outcome to HTTP. The name
/// and URL rules, the duplicate check and the audit are the use case's and the Domain's; the
/// refusals that carry a sentence are <c>DomainValidationException</c> and
/// <c>DomainConflictException</c>, which <c>DomainExceptionHandler</c> answers as <c>400</c> and
/// <c>409</c> with the Domain's own caller-safe messages.</para>
/// </summary>
[ApiController]
[Route("api/super-admin/supplier-presets")]
// Both, and both are enforced: the default policy carries the delegated-scope requirement that
// [RequiredScope] declares, and the named policy carries the platform-admin one. A named policy
// alone would replace the default rather than add to it, which would leave the scope undemanded.
[Authorize]
[Authorize(Policy = PlatformAdminPolicy.Name)]
[RequiredScope("access_as_user")]
[PlatformDiagnosticsEndpoint]
public sealed class SuperAdminSupplierPresetsController : ControllerBase
{
    private readonly ListSupplierPresets _list;
    private readonly CreateSupplierPreset _create;
    private readonly UpdateSupplierPreset _update;

    public SuperAdminSupplierPresetsController(
        ListSupplierPresets list,
        CreateSupplierPreset create,
        UpdateSupplierPreset update)
    {
        _list = list;
        _create = create;
        _update = update;
    }

    /// <summary>
    /// Every preset, enabled and disabled alike, ordered by name. It takes no filter, page, sort or
    /// search parameter, and the disabled ones are included deliberately: a preset nobody can see is
    /// a preset nobody can re-enable.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<SuperAdminSupplierPresetResponse>>> List(
        CancellationToken cancellationToken)
    {
        var presets = await _list.Handle(cancellationToken);

        return Ok(presets.Select(SuperAdminSupplierPresetResponse.From).ToList());
    }

    /// <summary>Adds one preset, enabled.</summary>
    /// <response code="201">The preset as it was stored.</response>
    /// <response code="400">
    /// The name is blank or longer than 200 characters, the website is not an absolute http(s)
    /// address or is longer than 2048 characters, or the served states or the price connector and its
    /// key are not a combination the supplier-master rules accept (issue #931). Nothing was written.
    /// </response>
    /// <response code="409">
    /// Another preset already holds this name, ignoring case. Nothing was written, and this is the
    /// answer whether the name was already there or arrived from another request in between.
    /// </response>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<SuperAdminSupplierPresetResponse>> Create(
        SuperAdminCreateSupplierPresetRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var created = await _create.Handle(request.ToSubmission(), cancellationToken);

        return CreatedAtAction(
            nameof(List),
            SuperAdminSupplierPresetResponse.From(created));
    }

    /// <summary>
    /// Edits one preset: its name, its website, what it serves, whether its prices include GST, how
    /// they are collected and whether it is still suggested, all together. Enabling and disabling is
    /// this call.
    /// </summary>
    /// <response code="200">The preset as the edit left it.</response>
    /// <response code="400">
    /// The name, the website, the served states or the connector and its key failed their rule.
    /// Nothing was written.
    /// </response>
    /// <response code="404">No such preset.</response>
    /// <response code="409">
    /// Another preset already holds this name, ignoring case. Nothing was written.
    /// </response>
    [HttpPut("{id:int}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<SuperAdminSupplierPresetResponse>> Update(
        int id,
        SuperAdminUpdateSupplierPresetRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var updated = await _update.Handle(
            id,
            request.ToSubmission(),
            request.IsEnabled,
            cancellationToken);

        return updated is null
            ? NotFound()
            : Ok(SuperAdminSupplierPresetResponse.From(updated));
    }
}
