using Inventory.Application.PlatformOptions;
using InventoryApi.Auth.PlatformAdmin;
using InventoryApi.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

/// <summary>
/// The Super Admin's switch over the one option whose visibility is switchable (issue #873):
/// Historical GST Classification, the occasional maintenance tool of issue #433.
///
/// <para><strong>Authorisation is the other Super Admin surfaces', unchanged.</strong>
/// <c>[Authorize(Policy = PlatformAdminPolicy.Name)]</c> is evaluated by the authorization
/// middleware before this action runs, and <c>BusinessScopeMiddleware</c> re-evaluates the same
/// policy before it allows the request past the membership requirement. That is class 2 of the four
/// ways an endpoint may declare its authorization (<c>EndpointAuthorizationClassificationTests</c>),
/// and both halves are required: the marker alone grants nothing, and the policy alone would not be
/// eligible for the membership bypass. There is no token claim, header, route value or body field
/// that selects the authority: it is the configured Entra <c>(tid, oid)</c> pair and nothing else,
/// and both values empty - the shipped state - means nobody can reach this controller at all. An
/// Owner of a business is refused here exactly as any other member is, because a business role is
/// not platform authority. A role preview is refused by <c>RolePreviewMiddleware</c>, like every
/// other write in the application.</para>
///
/// <para><strong>No business is involved, in either direction.</strong> The setting is one shared
/// application-wide answer: it names no business, carries no business key and cannot be made per
/// business by anything a request sends. The request carries a <em>denied</em>
/// <c>BusinessScope</c>, so the tenant query filters and <c>BusinessOwnershipEnforcer</c> stay fully
/// in force and nothing tenant-owned is readable or writable through it.</para>
///
/// <para><strong>Visibility only, and there is deliberately no <c>GET</c> here.</strong> The switch
/// hides an option; it grants and revokes nothing, and the classification tool's own endpoints keep
/// authorizing every request by <c>Data.Repair</c> exactly as issue #433 left them. The current state
/// is published to every signed-in caller - the administrator included - by
/// <c>GET /api/me/access</c>, which is where the application shell already reads what it may offer,
/// so a second read endpoint here would be a second answer to one question.</para>
/// </summary>
[ApiController]
[Route("api/super-admin/option-visibility")]
// Both, and both are enforced: the default policy carries the delegated-scope requirement that
// [RequiredScope] declares, and the named policy carries the platform-admin one. A named policy
// alone would replace the default rather than add to it, which would leave the scope undemanded.
[Authorize]
[Authorize(Policy = PlatformAdminPolicy.Name)]
[RequiredScope("access_as_user")]
[PlatformDiagnosticsEndpoint]
public sealed class SuperAdminOptionVisibilityController : ControllerBase
{
    private readonly SetHistoricalGstClassificationVisibility _setHistoricalGstClassification;

    public SuperAdminOptionVisibilityController(
        SetHistoricalGstClassificationVisibility setHistoricalGstClassification)
    {
        _setHistoricalGstClassification = setHistoricalGstClassification;
    }

    /// <summary>
    /// Switches Historical GST Classification on or off for everybody but a Super Admin.
    /// </summary>
    /// <response code="200">The setting as it was stored.</response>
    [HttpPut]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<SuperAdminOptionVisibilityResponse>> Put(
        SuperAdminOptionVisibilityRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var stored = await _setHistoricalGstClassification.Handle(
            request.HistoricalGstClassificationVisible,
            cancellationToken);

        return Ok(SuperAdminOptionVisibilityResponse.From(stored));
    }
}
