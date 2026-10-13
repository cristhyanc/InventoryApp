using Inventory.Application.PlatformAdmin;
using InventoryApi.Auth.PlatformAdmin;
using InventoryApi.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

/// <summary>
/// The Super Admin's business directory and the two decisions on a business application (issue
/// #544, one slice of #509 under #328): the first platform-admin surface that <em>writes</em>.
///
/// <para><strong>Authorisation is the diagnostics surface's, unchanged and server-side on every
/// request.</strong> <c>[Authorize(Policy = PlatformAdminPolicy.Name)]</c> is evaluated by the
/// authorization middleware before any action here runs, and <c>BusinessScopeMiddleware</c>
/// re-evaluates the same policy before it allows the request past the membership requirement. That
/// is class 2 of the four ways an endpoint may declare its authorization
/// (<c>EndpointAuthorizationClassificationTests</c>), and both halves are required: the marker
/// alone grants nothing, and the policy alone would not be eligible for the membership bypass. There
/// is no token claim, no header, no route value and no body field that selects the authority: it is
/// the configured Entra <c>(tid, oid)</c> pair and nothing else, and both values empty - the shipped
/// state - means nobody can reach this controller at all. An Owner of a business is refused here
/// exactly as any other member is: a business role is not platform authority.</para>
///
/// <para><strong>The bypass is of the membership requirement only.</strong> The request carries a
/// <em>denied</em> <c>BusinessScope</c>, so the tenant query filters and
/// <c>BusinessOwnershipEnforcer</c> stay fully in force and nothing tenant-owned is readable or
/// writable through it. What this surface reaches is the two structurally non-tenant-owned tables -
/// businesses and memberships - plus one column of the Nayax connection table, read-only; see
/// <c>Inventory.Infrastructure.Persistence.EfPlatformBusinessDirectoryStore</c> and
/// docs/architecture.md § Super Admin businesses API.</para>
///
/// <para><strong>The business id in the route is the subject of a decision, not a tenancy
/// scope.</strong> Every other endpoint in this application resolves its business from the
/// authenticated actor's membership and accepts no owner from a request (AGENTS.md § Tenant
/// ownership). A platform decision has to name the application it is about, so these two routes
/// carry the id - and it chooses nothing else: it resolves no business for the request, it is never
/// stamped on a row, and it cannot reach a tenant-owned table. It is checked against the stored rows
/// on every call, so an unknown id is <c>404</c> and an already-decided one is <c>409</c>.</para>
///
/// <para>The controller is thin, as every controller here is: it binds the route value and the one
/// optional body field, invokes the use case with the request's cancellation token, and maps the
/// outcome to HTTP. No rule, transaction, eligibility check or EF query lives here, and the
/// refusals that carry a sentence are <c>DomainValidationException</c> and
/// <c>DomainConflictException</c>, which <c>DomainExceptionHandler</c> answers as <c>400</c> and
/// <c>409</c> with the Domain's own caller-safe messages.</para>
/// </summary>
[ApiController]
[Route("api/super-admin/businesses")]
// Both, and both are enforced: the default policy carries the delegated-scope requirement that
// [RequiredScope] declares, and the named policy carries the platform-admin one. A named policy
// alone would replace the default rather than add to it, which would leave the scope undemanded -
// so "platform authority is an additional requirement on a delegated API call, never a replacement
// for one" is written here as two attributes rather than as a comment.
[Authorize]
[Authorize(Policy = PlatformAdminPolicy.Name)]
[RequiredScope("access_as_user")]
[PlatformDiagnosticsEndpoint]
public sealed class SuperAdminBusinessesController : ControllerBase
{
    private readonly ListPlatformBusinesses _list;
    private readonly CountPendingBusinessApplications _countPending;
    private readonly ApproveBusinessApplication _approve;
    private readonly RejectBusinessApplication _reject;

    public SuperAdminBusinessesController(
        ListPlatformBusinesses list,
        CountPendingBusinessApplications countPending,
        ApproveBusinessApplication approve,
        RejectBusinessApplication reject)
    {
        _list = list;
        _countPending = countPending;
        _approve = approve;
        _reject = reject;
    }

    /// <summary>
    /// Every business, pending applications first. The response carries the directory fields of
    /// <see cref="SuperAdminBusinessResponse"/> and no tenant data of any kind, and it takes no
    /// filter, page, sort or search parameter - a caller-supplied predicate is how a fixed
    /// cross-business list would become a query engine.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<SuperAdminBusinessResponse>>> List(
        CancellationToken cancellationToken)
    {
        var businesses = await _list.Handle(cancellationToken);

        return Ok(businesses.Select(SuperAdminBusinessResponse.From).ToList());
    }

    /// <summary>How many applications are waiting for a decision.</summary>
    [HttpGet("pending-count")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<SuperAdminPendingCountResponse>> PendingCount(
        CancellationToken cancellationToken) =>
        Ok(new SuperAdminPendingCountResponse(await _countPending.Handle(cancellationToken)));

    /// <summary>
    /// Approves one pending application: the business becomes <c>Active</c>, which is the one status
    /// that resolves, so its Owner's next request is served rather than refused.
    /// </summary>
    /// <response code="200">The approval was recorded.</response>
    /// <response code="404">No such business.</response>
    /// <response code="409">
    /// The application is no longer waiting for a decision, its applicant's membership in it is no
    /// longer active, or that applicant now holds an active membership in another business. Nothing
    /// was written.
    /// </response>
    [HttpPost("{businessId:int}/approve")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<SuperAdminBusinessDecisionResponse>> Approve(
        int businessId,
        CancellationToken cancellationToken)
    {
        var decision = await _approve.Handle(businessId, cancellationToken);

        return decision is null
            ? NotFound()
            : Ok(SuperAdminBusinessDecisionResponse.From(decision));
    }

    /// <summary>
    /// Refuses one pending application: the business becomes <c>Rejected</c> with the optional
    /// reason and the decision instant, and the applicant's membership is released so they are free
    /// to apply again.
    /// </summary>
    /// <response code="200">The rejection was recorded.</response>
    /// <response code="400">The reason is longer than 500 characters. Nothing was written.</response>
    /// <response code="404">No such business.</response>
    /// <response code="409">
    /// The application is no longer waiting for a decision. Nothing was written.
    /// </response>
    [HttpPost("{businessId:int}/reject")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<SuperAdminBusinessDecisionResponse>> Reject(
        int businessId,
        SuperAdminRejectBusinessRequest? request,
        CancellationToken cancellationToken)
    {
        // The body is optional on purpose: a rejection needs no reason, so a caller who sends none
        // at all is refusing the application rather than making a malformed request.
        var decision = await _reject.Handle(businessId, request?.Reason, cancellationToken);

        return decision is null
            ? NotFound()
            : Ok(SuperAdminBusinessDecisionResponse.From(decision));
    }
}
