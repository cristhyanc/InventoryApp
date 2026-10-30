using Inventory.Application.Access;
using Inventory.Application.Subscriptions;
using Inventory.Domain.ConnectedMachines;
using InventoryApi.Auth;
using InventoryApi.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

/// <summary>
/// The signed-in member's own access (issue #521).
///
/// There is no route, query or body parameter: the member and their business come from the
/// authenticated actor's membership, through the issue #64 current-business abstraction, so no
/// request value can choose whose access is described. It reports the caller and nothing else -
/// no other member, no other business, and no identifier for either.
///
/// <c>GET access</c> carries exactly the requirements every other business endpoint carries:
/// authentication, the delegated <c>access_as_user</c> scope, and the membership check
/// <c>BusinessScopeMiddleware</c> applies before any action runs. Deliberately no capability
/// requirement of its own: a member has to be able to ask what they may do, whatever role they
/// hold. Enforcing capabilities on the other endpoints is issue #502's work.
///
/// Since issue #551 the same action also answers a platform administrator reading this business
/// through a support session - the one caller here who holds no role - with that session's own
/// read-only capability set and a <c>supportSession</c> block saying whose data it is and when the
/// session runs out. It still has no capability requirement of its own, for the same reason: a
/// supporter has to be able to ask what this view permits.
///
/// <c>GET account-state</c> (issue #523) is the one action here that a person without a usable
/// membership may reach, through <see cref="MembershipNotRequiredEndpointAttribute"/>. That
/// exemption is per action and is not a property of this controller: every other action on it,
/// now and in future, keeps the membership requirement, and the marked action still requires
/// authentication and the same delegated scope and still runs with a denied business scope.
/// </summary>
[ApiController]
[Route("api/me")]
[Authorize]
[RequiredScope("access_as_user")]
public sealed class MeController : ControllerBase
{
    private readonly GetCurrentAccess _getCurrentAccess;
    private readonly GetAccountState _getAccountState;
    private readonly GetPlanCatalogue _getPlanCatalogue;

    public MeController(
        GetCurrentAccess getCurrentAccess,
        GetAccountState getAccountState,
        GetPlanCatalogue getPlanCatalogue)
    {
        _getCurrentAccess = getCurrentAccess;
        _getAccountState = getAccountState;
        _getPlanCatalogue = getPlanCatalogue;
    }

    [HttpGet("access")]
    public async Task<ActionResult<MeAccessResponse>> Access(CancellationToken cancellationToken)
    {
        var access = await _getCurrentAccess.Handle(cancellationToken);

        return Ok(new MeAccessResponse(
            access.Role?.ToString(),
            [.. access.Capabilities.Select(Capabilities.Name)],
            access.BusinessName,
            access.TimeZoneId,
            access.SupportSession is { } session
                ? new MeSupportSessionResponse(
                    session.BusinessName,
                    session.ExpiresAtUtc,
                    session.ReadOnly)
                : null,
            access.HistoricalGstClassificationVisible,
            new MeEntitlementResponse(
                access.Entitlement.Enforced,
                access.Entitlement.Plan?.ToString(),
                access.Entitlement.Source.ToString(),
                access.Entitlement.EndsAtUtc,
                access.AttachmentStorage is { } storage
                    ? new MeAttachmentStorageResponse(
                        storage.UsedBytes,
                        storage.LimitBytes,
                        storage.WarningLevel.ToString())
                    : null,
                ConnectedMachines(access.Entitlement.ConnectedMachines))));
    }

    /// <summary>
    /// The Free/Starter/Pro catalogue (issue #970): every plan's advertised price, storage allowance
    /// and connected-machine terms, from <see cref="GetPlanCatalogue"/> - the Application read over
    /// the one definition every plan-aware surface shares, so this page never hard-codes a price or
    /// a limit of its own. This action binds nothing, decides nothing and maps the use case's
    /// result, exactly as every other action here does. It names no business and carries no
    /// entitlement fact; what the business itself has bought is <see cref="Access"/>'s own answer,
    /// read separately.
    ///
    /// It declares <see cref="Capability.DashboardView"/>, which every role and a support session
    /// hold, because the catalogue - like the price and storage figures the plan page shows beside
    /// it - is visible to every member rather than gated by what a role may do.
    /// </summary>
    [HttpGet("plan-catalogue")]
    [RequireCapability(Capability.DashboardView)]
    public ActionResult<MePlanCatalogueResponse> PlanCatalogue()
    {
        return Ok(new MePlanCatalogueResponse(
            [.. _getPlanCatalogue.Handle().Select(definition => new MePlanCatalogueEntryResponse(
                definition.Plan.ToString(),
                definition.MonthlyPriceMinorUnits,
                definition.CurrencyCode,
                definition.AttachmentStorageLimitBytes,
                definition.IncludedConnectedMachines,
                definition.AdditionalConnectedMachinePriceMinorUnits))]));
    }

    /// <summary>
    /// Projects the connected-machine answer of issue #978, or <see langword="null"/> while nothing is
    /// enforced and there is therefore nothing to report. It formats and never calculates: the
    /// allowance, the count, its age, its state and how many machines are over the allowance are all
    /// decided by <c>Inventory.Domain.ConnectedMachines.ConnectedMachineCountPolicy</c>.
    /// </summary>
    private static MeConnectedMachinesResponse? ConnectedMachines(
        ConnectedMachineEntitlement? connectedMachines) =>
        connectedMachines is null
            ? null
            : new MeConnectedMachinesResponse(
                connectedMachines.IncludedByPlan,
                connectedMachines.PurchasedAdditional,
                connectedMachines.Allowance,
                connectedMachines.Count,
                connectedMachines.AsOfDateUtc,
                connectedMachines.AgeInDays,
                connectedMachines.State.ToString(),
                connectedMachines.UnavailableReason.ToString(),
                connectedMachines.MachinesOverAllowance);

    /// <summary>
    /// Which state the caller's own account is in (issue #523), so a person the other endpoints
    /// refuse can be shown the screen that explains it rather than a bare 403.
    ///
    /// It takes no parameter, describes the caller alone, and names no business: the states are
    /// about the caller's membership, and the middleware's public 403 body is unchanged by this
    /// endpoint existing. The state is reported as its published name; <c>onboardingEnabled</c>
    /// and <c>rejectionReason</c> are decided by the use case.
    /// </summary>
    [HttpGet("account-state")]
    [MembershipNotRequiredEndpoint]
    public async Task<ActionResult<MeAccountStateResponse>> AccountState(CancellationToken cancellationToken)
    {
        var accountState = await _getAccountState.Handle(cancellationToken);

        return Ok(new MeAccountStateResponse(
            accountState.State.ToString(),
            accountState.OnboardingEnabled,
            accountState.RejectionReason));
    }
}
