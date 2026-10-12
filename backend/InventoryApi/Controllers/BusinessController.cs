using Inventory.Application.Access;
using Inventory.Application.Businesses;
using Inventory.Domain.Tenancy;
using InventoryApi.Auth;
using InventoryApi.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

/// <summary>
/// The signed-in operator's own business (issue #499), and the Owner's settings for it (issue
/// #548).
///
/// <c>GET current</c> exists so the frontend can render operator-facing instants and resolve
/// calendar inputs in the business's own time zone instead of a hard-coded constant. There is no
/// route, query or body parameter on any action here: the business comes from the authenticated
/// actor's membership, through the issue #64 current-business abstraction, so no request value can
/// choose whose business is described or changed.
///
/// On top of the requirements every business endpoint carries - authentication, the delegated
/// <c>access_as_user</c> scope and the membership check <c>BusinessScopeMiddleware</c> applies
/// before any action runs - each action declares its own capability (issue #525), because the two
/// surfaces here are deliberately not the same rule:
/// <list type="bullet">
///   <item><c>GET current</c> requires <c>Dashboard.View</c>, which every role holds: the name and
///   time zone it reports are what renders the application's own shell, so anybody who may see a
///   page needs them.</item>
///   <item><c>GET</c> and <c>PUT settings</c> require <c>Business.Manage</c>, which only an Owner
///   holds (issue #526). The read is restricted with the write because it reports the business's
///   contact address, ABN and GST registration - the business's own tax and contact detail, not
///   something every member needs to render a page.</item>
/// </list>
/// </summary>
[ApiController]
[Route("api/business")]
[Authorize]
[RequiredScope("access_as_user")]
public sealed class BusinessController : ControllerBase
{
    private readonly GetCurrentBusiness _getCurrentBusiness;
    private readonly GetBusinessSettings _getSettings;
    private readonly UpdateBusinessSettings _updateSettings;

    public BusinessController(
        GetCurrentBusiness getCurrentBusiness,
        GetBusinessSettings getSettings,
        UpdateBusinessSettings updateSettings)
    {
        _getCurrentBusiness = getCurrentBusiness;
        _getSettings = getSettings;
        _updateSettings = updateSettings;
    }

    [HttpGet("current")]
    [RequireCapability(Capability.DashboardView)]
    public async Task<ActionResult<CurrentBusinessResponse>> Current(CancellationToken cancellationToken)
    {
        var business = await _getCurrentBusiness.Handle(cancellationToken);
        return Ok(new CurrentBusinessResponse(business.Name, business.TimeZoneId));
    }

    [HttpGet("settings")]
    [RequireCapability(Capability.BusinessManage)]
    public async Task<ActionResult<BusinessSettingsResponse>> Settings(CancellationToken cancellationToken)
    {
        var settings = await _getSettings.Handle(cancellationToken);
        return Ok(ToResponse(settings));
    }

    /// <summary>
    /// Saves every setting at once. A validation failure is answered <c>400</c> by
    /// <c>DomainExceptionHandler</c> from the message the Domain rule - or the central time-zone
    /// enforcer - refused it with, and stores nothing.
    /// </summary>
    [HttpPut("settings")]
    [RequireCapability(Capability.BusinessManage)]
    public async Task<ActionResult<BusinessSettingsResponse>> SaveSettings(
        BusinessSettingsRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var saved = await _updateSettings.Handle(
            new BusinessSettingsSubmission(
                request.Name,
                request.ContactEmail,
                request.Abn,
                request.GstRegistered,
                request.TimeZoneId),
            cancellationToken);

        return Ok(ToResponse(saved));
    }

    private static BusinessSettingsResponse ToResponse(BusinessSettings settings) =>
        new(settings.Name, settings.ContactEmail, settings.Abn, settings.GstRegistered, settings.TimeZoneId);
}
