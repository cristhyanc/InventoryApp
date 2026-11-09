using Inventory.Application.PlatformOptions;
using InventoryApi.Auth.PlatformAdmin;
using InventoryApi.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

/// <summary>
/// The Super Admin's switches over the Admin maintenance destinations (issue #1148), which supersede
/// the single switch of issue #873: a closed catalogue of seven keys, each offered to everybody else
/// or not.
///
/// <para><strong>Authorisation is the other Super Admin surfaces', unchanged.</strong>
/// <c>[Authorize(Policy = PlatformAdminPolicy.Name)]</c> is evaluated by the authorization
/// middleware before any action here runs, and <c>BusinessScopeMiddleware</c> re-evaluates the same
/// policy before it allows the request past the membership requirement. That is class 2 of the five
/// ways an endpoint may declare its authorization (<c>EndpointAuthorizationClassificationTests</c>),
/// and both halves are required: the marker alone grants nothing, and the policy alone would not be
/// eligible for the membership bypass. There is no token claim, header, route value or body field
/// that selects the authority: it is the configured Entra <c>(tid, oid)</c> pair and nothing else,
/// and both values empty - the shipped state - means nobody can reach this controller at all. An
/// Owner of a business is refused here exactly as any other member is, because a business role is
/// not platform authority. A role preview is refused on the write by
/// <c>RolePreviewMiddleware</c>, like every other write in the application.</para>
///
/// <para><strong>No business is involved, in either direction.</strong> The settings are one shared
/// application-wide answer per destination: they name no business, carry no business key and cannot
/// be made per business by anything a request sends. The request carries a <em>denied</em>
/// <c>BusinessScope</c>, so the tenant query filters and <c>BusinessOwnershipEnforcer</c> stay fully
/// in force and nothing tenant-owned is readable or writable through it.</para>
///
/// <para><strong>The key is the only thing a caller chooses, and it chooses from a closed
/// catalogue.</strong> <see cref="MenuVisibilityCatalogue"/> is compiled, so a key it does not
/// declare is answered <c>404</c> with nothing stored - this is deliberately not a feature-flag
/// framework, and a caller cannot create a setting. The write names one key, so switching one
/// destination never rewrites another's answer.</para>
///
/// <para><strong>There <em>is</em> a <c>GET</c> here, unlike on the superseded single-option
/// surface.</strong> It answers the question only this surface has - "what can be switched, and what
/// is each one's state now" - which the catalogue's labels and the defaulted keys make
/// unanswerable from <c>GET /api/me/access</c>. That read still publishes what every signed-in
/// caller needs, the keys that are switched off, and remains the one answer the application shell
/// builds its menu from.</para>
///
/// <para><strong>Visibility only.</strong> A switch hides a destination; it grants and revokes
/// nothing, and every one of those destinations' endpoints keeps authorizing each request by its own
/// capability, plan and provider rules exactly as before.</para>
/// </summary>
[ApiController]
[Route("api/super-admin/menu-visibility")]
// Both, and both are enforced: the default policy carries the delegated-scope requirement that
// [RequiredScope] declares, and the named policy carries the platform-admin one. A named policy
// alone would replace the default rather than add to it, which would leave the scope undemanded.
[Authorize]
[Authorize(Policy = PlatformAdminPolicy.Name)]
[RequiredScope("access_as_user")]
[PlatformDiagnosticsEndpoint]
public sealed class SuperAdminMenuVisibilityController : ControllerBase
{
    private readonly GetMenuVisibility _get;
    private readonly SetMenuVisibility _set;

    public SuperAdminMenuVisibilityController(GetMenuVisibility get, SetMenuVisibility set)
    {
        _get = get;
        _set = set;
    }

    /// <summary>
    /// Every switchable destination with its label and the state it is in now, stored or defaulted.
    /// </summary>
    /// <response code="200">The whole catalogue, in its own order.</response>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<SuperAdminMenuVisibilityResponse>> Get(
        CancellationToken cancellationToken)
    {
        var visibility = await _get.Handle(cancellationToken);

        return Ok(SuperAdminMenuVisibilityResponse.From(visibility));
    }

    /// <summary>
    /// Switches one destination on or off for everybody but a Super Admin.
    /// </summary>
    /// <response code="200">That destination as it was stored.</response>
    /// <response code="404">The catalogue declares no such key; nothing was stored.</response>
    [HttpPut("{key}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SuperAdminMenuVisibilityOptionResponse>> Put(
        string key,
        SuperAdminMenuVisibilityRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var stored = await _set.Handle(key, request.Visible, cancellationToken);

        return stored is null
            ? NotFound()
            : Ok(SuperAdminMenuVisibilityOptionResponse.From(stored));
    }
}
