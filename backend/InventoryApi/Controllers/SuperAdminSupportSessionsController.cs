using Inventory.Application.SupportSessions;
using InventoryApi.Auth.PlatformAdmin;
using InventoryApi.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

/// <summary>
/// The Super Admin's read-only support sessions (issue #550, a slice of #512 under #328): start one
/// for a business that is reporting a problem, read the one in progress, and end it.
///
/// <para><strong>What this surface does, and deliberately does not do.</strong> It stores the record
/// that a platform administrator may read one business's data for an hour, reports it and ends it,
/// and nothing more. Resolving a business from that record is <c>RequestSupportSessionAccessor</c>'s,
/// on the request that presents the session id (issue #551), so *holding* a session grants nothing:
/// these endpoints never carry the header and are always reached as the administrator themselves.
/// Issue #550 shipped the audited record before anything acted on it, which was the fail-closed order
/// to build this in.</para>
///
/// <para><strong>Authorisation is the platform-admin surface's, unchanged.</strong>
/// <c>[Authorize]</c>, <c>[Authorize(Policy = PlatformAdminPolicy.Name)]</c>,
/// <c>[RequiredScope("access_as_user")]</c> and <see cref="PlatformDiagnosticsEndpointAttribute"/> -
/// class 2 of the four ways an endpoint may declare its authorization
/// (<c>EndpointAuthorizationClassificationTests</c>), the same marker and the same policy as the
/// diagnostics API and the Super Admin businesses API. Both halves are required: the marker alone
/// grants nothing, and the policy alone would not be eligible for the membership bypass. Authority is
/// the configured Entra <c>(tid, oid)</c> pair and nothing else, so an Owner is refused here exactly
/// as any other member is, and with the pair unset - the shipped state - nobody reaches this
/// controller at all.</para>
///
/// <para><strong>These three endpoints never read the support-session header.</strong> They are the
/// control surface, and they are reached as the administrator themselves: the session they act on is
/// the one belonging to the authenticated actor, found server-side, so there is nothing for a request
/// to present and no session of anybody else's to name. The header is #551's, for the requests that
/// are made <em>inside</em> a session.</para>
///
/// <para><strong>The business id in the route is the subject of the session, not a tenancy
/// scope.</strong> It is the same reviewed shape as the Super Admin businesses API's route values
/// (docs/architecture.md § Super Admin businesses API): the request still carries a <em>denied</em>
/// <c>BusinessScope</c>, the id resolves no business for it, nothing is stamped with it, and it cannot
/// reach a tenant-owned table. It is checked against the stored rows on every call, so an unknown id
/// is <c>404</c> and a business that is not <c>Active</c> is <c>409</c>. It is in the route rather
/// than in a body on purpose: no DTO in this application carries a business id inwards.</para>
///
/// <para>The controller is thin, as every controller here is: it binds the route value and the one
/// body field, invokes the use case with the request's cancellation token, and maps the outcome to
/// HTTP. The refusals that carry a sentence are <c>DomainValidationException</c> and
/// <c>DomainConflictException</c>, which <c>DomainExceptionHandler</c> answers as <c>400</c> and
/// <c>409</c> with the Domain's own caller-safe messages.</para>
/// </summary>
[ApiController]
[Route("api/super-admin/support-sessions")]
// Both, and both are enforced, for the reason the businesses controller states: the default policy
// carries the delegated-scope requirement, and the named policy carries the platform-admin one. A
// named policy alone would replace the default rather than add to it.
[Authorize]
[Authorize(Policy = PlatformAdminPolicy.Name)]
[RequiredScope("access_as_user")]
[PlatformDiagnosticsEndpoint]
public sealed class SuperAdminSupportSessionsController : ControllerBase
{
    private readonly StartSupportSession _start;
    private readonly GetActiveSupportSession _active;
    private readonly EndSupportSession _end;

    public SuperAdminSupportSessionsController(
        StartSupportSession start,
        GetActiveSupportSession active,
        EndSupportSession end)
    {
        _start = start;
        _active = active;
        _end = end;
    }

    /// <summary>
    /// Starts a support session for one <c>Active</c> business, with a required reason, and answers
    /// with the session id, the business's name and the expiry.
    ///
    /// <para>The session id is returned here and nowhere else. The expiry is the server's: 60 minutes
    /// from the start, which is both the default and the maximum, and the request carries no duration
    /// at all.</para>
    /// </summary>
    /// <response code="200">The session was started.</response>
    /// <response code="400">The reason is missing, blank or longer than 500 characters. Nothing was written.</response>
    /// <response code="404">No such business.</response>
    /// <response code="409">
    /// The business is not active, or this administrator already has an active session - one at a
    /// time. Nothing was written in either case.
    /// </response>
    [HttpPost("{businessId:int}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<SuperAdminStartedSupportSessionResponse>> Start(
        int businessId,
        SuperAdminStartSupportSessionRequest? request,
        CancellationToken cancellationToken)
    {
        // The body is bound as optional so that a request with none at all is refused by the Domain's
        // own "a session needs a reason" sentence, rather than by a model-binding error that says
        // less.
        var started = await _start.Handle(businessId, request?.Reason, cancellationToken);

        return started is null
            ? NotFound()
            : Ok(SuperAdminStartedSupportSessionResponse.From(started));
    }

    /// <summary>
    /// The caller's own active session, or nothing when they have none.
    ///
    /// <para>It reports the business, its name, the reason and both instants - never the session id,
    /// which exists in the start response only. An expired session is "none" without anything having
    /// had to run.</para>
    /// </summary>
    /// <response code="200">The caller's active session.</response>
    /// <response code="204">The caller has no active session, which is not an error.</response>
    [HttpGet("active")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult<SuperAdminActiveSupportSessionResponse>> Active(
        CancellationToken cancellationToken)
    {
        var active = await _active.Handle(cancellationToken);

        // No content rather than 404: "you have no session" is an answer about the caller, and a 404
        // would say the endpoint does not exist.
        return active is null
            ? NoContent()
            : Ok(SuperAdminActiveSupportSessionResponse.From(active));
    }

    /// <summary>
    /// Ends the caller's own session, whichever business it was reading.
    ///
    /// <para>It takes no session id: the session is the authenticated actor's, so there is nothing a
    /// request could present and no way to end somebody else's. Ending when nothing is active is a
    /// success that writes and audits nothing - including for a session that had already expired or
    /// that the deactivation of its business had closed.</para>
    /// </summary>
    /// <response code="200">The session as the end left it.</response>
    /// <response code="204">There was no active session to end.</response>
    [HttpPost("active/end")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult<SuperAdminEndedSupportSessionResponse>> End(
        CancellationToken cancellationToken)
    {
        var ended = await _end.Handle(cancellationToken);

        return ended is null
            ? NoContent()
            : Ok(SuperAdminEndedSupportSessionResponse.From(ended));
    }
}
