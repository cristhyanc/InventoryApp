using Inventory.Application.PlatformPlans;
using InventoryApi.Auth.PlatformAdmin;
using InventoryApi.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

/// <summary>
/// The Super Admin's manual plan grant on one business (issue #982, child of #965, after #966): read
/// it, create or change it, and end it. It is how the platform owner grandfathers, trials and comps
/// a business <em>without billing it</em>.
///
/// <para><strong>Authorisation is the other Super Admin surfaces', unchanged.</strong>
/// <c>[Authorize(Policy = PlatformAdminPolicy.Name)]</c> is evaluated by the authorization middleware
/// before any action here runs, and <c>BusinessScopeMiddleware</c> re-evaluates the same policy
/// before it allows the request past the membership requirement - class 2 of the five ways an
/// endpoint may declare its authorization (<c>EndpointAuthorizationClassificationTests</c>), with
/// both halves required. There is no token claim, header, route value or body field that selects the
/// authority: it is the configured Entra <c>(tid, oid)</c> pair and nothing else, and both values
/// empty - the shipped state - means nobody can reach this controller at all. An Owner of a business
/// is refused here exactly as a Manager or an Operator is, because a business role is not platform
/// authority. A role preview is refused by <c>RolePreviewMiddleware</c> like every other write, and a
/// read-only support session is refused by its own HTTP-method rule and again by
/// <c>BusinessOwnershipEnforcer</c>.</para>
///
/// <para><strong>What it may write is one record, and nothing of the business's own data.</strong>
/// The request carries a <em>denied</em> <c>BusinessScope</c> throughout. What this surface reaches is
/// the business row it names, that business's manual grant, and the append-only audit row written in
/// the same transaction as the grant - through <see cref="IPlatformPlanGrantStore"/>, which is its own
/// narrow port and deliberately not a widening of the business directory store. No payment, billing
/// status or subscription record is readable or writable here; no sale, stock, cost, purchase,
/// product, machine, site, document or report value is either; and nothing is ever deleted.</para>
///
/// <para><strong>The business id in the route is the subject of the decision, not a tenancy
/// scope.</strong> It is checked against the stored business row inside the write transaction - so an
/// unknown id is <c>404</c> - and the write is addressed to the key that row carries. Each write
/// presents the revision the caller read, and a stale one is <c>409</c>, so two Super Admin tabs
/// cannot overwrite each other.</para>
///
/// <para>The controller is thin, as every controller here is: it binds the route value and the body,
/// invokes the use case with the request's cancellation token, and maps the outcome to HTTP. No rule,
/// transaction, revision comparison or EF query lives here, and the refusals that carry a sentence
/// are <c>DomainValidationException</c> and <c>DomainConflictException</c>, which
/// <c>DomainExceptionHandler</c> answers as <c>400</c> and <c>409</c> with the Domain's own
/// caller-safe messages.</para>
/// </summary>
[ApiController]
[Route("api/super-admin/businesses/{businessId:int}/plan-grant")]
// Both, and both are enforced: the default policy carries the delegated-scope requirement that
// [RequiredScope] declares, and the named policy carries the platform-admin one. A named policy
// alone would replace the default rather than add to it, which would leave the scope undemanded.
[Authorize]
[Authorize(Policy = PlatformAdminPolicy.Name)]
[RequiredScope("access_as_user")]
[PlatformDiagnosticsEndpoint]
public sealed class SuperAdminPlanGrantsController : ControllerBase
{
    private readonly ManageBusinessPlanGrant _grants;

    public SuperAdminPlanGrantsController(ManageBusinessPlanGrant grants)
    {
        _grants = grants;
    }

    /// <summary>
    /// One business's manual grant as it stands, with the revision the next write has to present.
    /// </summary>
    /// <response code="200">The grant, or the business with no grant at all.</response>
    /// <response code="404">No such business.</response>
    /// <response code="409">
    /// The business holds more than one manual grant, which this surface refuses to choose between.
    /// </response>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<SuperAdminPlanGrantResponse>> Get(
        int businessId,
        CancellationToken cancellationToken)
    {
        var view = await _grants.GetAsync(businessId, cancellationToken);

        return view is null ? NotFound() : Ok(SuperAdminPlanGrantResponse.From(view));
    }

    /// <summary>
    /// Creates the business's manual grant, or changes the one it holds: the plan, the optional last
    /// day it applies to, and the reason.
    /// </summary>
    /// <response code="200">The grant as it was written.</response>
    /// <response code="400">
    /// The plan is not Starter or Pro, the reason is missing or too long, or the end date is already
    /// over. Nothing was written.
    /// </response>
    /// <response code="404">No such business.</response>
    /// <response code="409">
    /// The revision is stale - the grant changed since it was read - or the business holds more than
    /// one manual grant. Nothing was written.
    /// </response>
    [HttpPut]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<SuperAdminPlanGrantResponse>> Put(
        int businessId,
        SuperAdminPlanGrantRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var view = await _grants.SetAsync(
            businessId,
            request.Plan,
            request.EndsOnUtc,
            request.Reason,
            request.Revision,
            cancellationToken);

        return view is null ? NotFound() : Ok(SuperAdminPlanGrantResponse.From(view));
    }

    /// <summary>
    /// Ends the business's manual grant now: it stops entitling from this instant, and the business
    /// falls back to whatever else entitles it - its paid subscription, or Free. The row is kept with
    /// its plan, its start, its granting reason and its new end instant; nothing is deleted.
    /// </summary>
    /// <response code="200">The grant as the end left it.</response>
    /// <response code="400">The reason is missing or too long. Nothing was written.</response>
    /// <response code="404">No such business.</response>
    /// <response code="409">
    /// The revision is stale, the business holds no grant that currently applies, or it holds more
    /// than one. Nothing was written.
    /// </response>
    [HttpPost("end")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<SuperAdminPlanGrantResponse>> End(
        int businessId,
        SuperAdminEndPlanGrantRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var view = await _grants.EndAsync(
            businessId,
            request.Reason,
            request.Revision,
            cancellationToken);

        return view is null ? NotFound() : Ok(SuperAdminPlanGrantResponse.From(view));
    }
}
