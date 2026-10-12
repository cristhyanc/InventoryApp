using System.Globalization;
using Inventory.Application.Onboarding;
using InventoryApi.Auth;
using InventoryApi.DTOs;
using InventoryApi.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

/// <summary>
/// Applying for a business (issue #541, one slice of #507 under #328): the endpoint a signed-in
/// person who belongs to no business calls to ask to start one.
///
/// <para><strong>Why this is not an action on <see cref="BusinessController"/>.</strong> That
/// controller is the Owner's own business - the shell read and the settings - and declares
/// capabilities a person applying for a business could not possibly hold, because they have no
/// membership and therefore no role. This endpoint has the opposite authorization shape, exactly as
/// <see cref="InviteRedemptionController"/> does, so it is its own controller with its own story
/// rather than an exception inside somebody else's.</para>
///
/// <para><strong>The authorization story, in full.</strong> The action carries
/// <see cref="MembershipNotRequiredEndpointAttribute"/> (issue #523) and is named on that marker's
/// reviewed allowlist in <c>EndpointAuthorizationClassificationTests</c>, which is class 3 of the
/// four ways an endpoint may be authorized. It still requires authentication and the delegated
/// <c>access_as_user</c> scope - "membership not required" is not "anonymous" - and it still runs
/// with a <em>denied</em> <c>BusinessScope</c>: nothing resolves a business for the request, the
/// tenant query filters and <c>BusinessOwnershipEnforcer</c> stay fully in force, and it reads no
/// business data. The two tables it reaches, businesses and memberships, are structurally
/// non-tenant-owned, and the business it writes in is the one it creates
/// (docs/architecture.md § Self-service business application).</para>
///
/// <para><strong>Nothing in the request chooses anything but the new business's own details.</strong>
/// The body carries the five settings the application records and nothing else: the applicant, their
/// role, the business's lifecycle status and its identifier are all decided by the server, the first
/// from the validated token the request authenticated with.</para>
///
/// <para><strong>It is behind a flag, and the flag's "off" answer is a bare <c>404</c>.</strong>
/// A deployment that has not switched self-service onboarding on has no such endpoint, which is
/// what a <c>404</c> says - no body, no message, nothing that distinguishes a disabled feature from
/// a route that was never there. <see cref="SelfServiceOnboardingGateAttribute"/> answers it before
/// MVC binds the body, so a malformed, empty or wrongly typed body cannot draw
/// <c>[ApiController]</c>'s automatic <c>400</c> out of a disabled deployment; it reads the same
/// Application-layer flag the use case re-checks, so the same value drives
/// <c>onboardingEnabled</c> on <c>GET /api/me/account-state</c>.</para>
///
/// <para>The controller is thin, as every controller here is: it binds the request, invokes the one
/// use case with the request's cancellation token, and maps the outcome to HTTP. No validation
/// rule, eligibility rule, rate-limit decision, transaction or EF query lives here, and the
/// refusals that carry a sentence are <c>DomainValidationException</c> and
/// <c>DomainConflictException</c>, which <see cref="DomainExceptionHandler"/> answers as
/// <c>400</c> and <c>409</c> with the Domain's own caller-safe messages.</para>
/// </summary>
[ApiController]
[Route("api/onboarding")]
[Authorize]
[RequiredScope("access_as_user")]
public sealed class OnboardingController : ControllerBase
{
    private readonly ApplyForBusiness _apply;

    public OnboardingController(ApplyForBusiness apply)
    {
        _apply = apply;
    }

    /// <summary>
    /// Submits the caller's own business application: it creates a <c>Pending</c> business with the
    /// submitted details and makes the caller its active Owner, in one transaction, so an
    /// application either records both or neither.
    ///
    /// Submitting the identical application again while it is still pending answers with the
    /// application that is already waiting and writes nothing, so a lost response or a double
    /// submission cannot create a second business.
    /// </summary>
    /// <response code="200">
    /// The application was recorded, or the identical one that was already waiting is reported back.
    /// </response>
    /// <response code="400">
    /// The submitted details break one of the application rules - a missing or over-long name, a
    /// contact address that cannot be an address, an unstated GST registration, a malformed ABN, or
    /// a missing or unresolvable time zone. Nothing was written.
    /// </response>
    /// <response code="404">Self-service onboarding is not switched on for this deployment.</response>
    /// <response code="409">
    /// The caller already holds an active business membership, or already has an application waiting
    /// that these details do not match. Nothing was written.
    /// </response>
    /// <response code="429">
    /// This caller has submitted too many applications too recently. <c>Retry-After</c> carries the
    /// wait, in seconds.
    /// </response>
    [HttpPost("business-applications")]
    [MembershipNotRequiredEndpoint]
    [SelfServiceOnboardingGate]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<BusinessApplicationResponse>> Apply(
        BusinessApplicationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var outcome = await _apply.Handle(
            new BusinessApplicationSubmission(
                request.Name,
                request.ContactEmail,
                request.Abn,
                request.GstRegistered,
                request.TimeZoneId),
            cancellationToken);

        if (!outcome.Enabled)
        {
            // The framework's own generic 404, with no detail of any kind. A problem detail saying
            // "self-service onboarding is disabled" would confirm both that the feature exists and
            // that this deployment chose not to run it, which is exactly what a 404 is here to
            // avoid saying.
            return NotFound();
        }

        return outcome.RetryAfter is { } retryAfter
            ? TooManyApplications(retryAfter)
            : Ok(BusinessApplicationResponse.From(outcome.Application!));
    }

    /// <summary>
    /// The refusal of the per-applicant submission limit.
    ///
    /// A fixed sentence with no count, no window and no business in it: how often this person has
    /// applied is not something a response needs to spell out.
    /// </summary>
    private ActionResult TooManyApplications(TimeSpan retryAfter)
    {
        // Rounded up, so a client that waits exactly this long is past the window rather than one
        // tick short of it, and never below one second.
        var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
        Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);

        return StatusCode(
            StatusCodes.Status429TooManyRequests,
            new ProblemDetails
            {
                Title = "Too many business applications",
                Detail = "You have submitted too many business applications recently. "
                    + "Wait a few minutes and try again.",
                Status = StatusCodes.Status429TooManyRequests,
            });
    }
}
