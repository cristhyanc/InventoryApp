using System.Globalization;
using Inventory.Application.Access;
using Inventory.Application.Nayax;
using Inventory.Domain.Subscriptions;
using InventoryApi.Auth;
using InventoryApi.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

/// <summary>
/// The HTTP surface of the business's own Nayax connection (issues #536 and #537, slices of #506
/// under #328): test a pasted operator id and token, save the tested pair, re-test the saved one,
/// and read the resulting state.
///
/// <para>The controller is thin, as every controller here is: it binds the request, invokes the one
/// use case with the request's cancellation token, and maps the outcome to HTTP. No Nayax call,
/// status mapping, encryption, expiry rule, rate-limit decision, transaction or EF query lives here.
/// Ownership is the central one - <c>AppDbContext</c>'s query filters and its ownership stamp on
/// save - so no route, body or header value can name an owner, and a stored draft belongs to the
/// business and the Owner that created it.</para>
///
/// <para><strong>The capability is declared per action, not on the controller.</strong> Three of
/// these actions configure the whole business's integration and need <c>Integration.Manage</c>,
/// which #328's table grants to an Owner alone; the fourth only reports whether Nayax is working and
/// needs <c>Dashboard.View</c>, which every role holds, because the banners of #539 are shown to a
/// Manager and an Operator who cannot reach this page at all. Capability requirements accumulate
/// down an endpoint, so a controller-level declaration would mean the status read required
/// <em>both</em> - and would make it Owner-only (issues #525 and #526). A denied call returns
/// <c>403</c> with no detail about the capability or the caller's role.</para>
///
/// <para>The three writing actions are <c>POST</c>s, and not only because they write: they submit a
/// secret or an identifier in a body rather than a URL, they call out to Nayax, and a cached
/// <c>GET</c> response would hand a caller a verdict about credentials it never tested. The status
/// read is the one <c>GET</c>: it is a read of the business's own configuration state and nothing
/// else.</para>
///
/// <para><strong>The plan requirement is declared per action too, on the same three</strong> (issue
/// #967). These are the only Nayax calls in the application that do not go through
/// <c>INayaxRequestCredentialProvider</c> - test authenticates with the pasted pair and save and
/// re-test run the connection check themselves - so the credential gate's plan check cannot cover
/// them and each declares <see cref="PlanFeature.SalesProviderIntegration"/> itself. It is evaluated
/// after the Owner capability and before the action, so a refused request makes no Nayax request, no
/// credential write and no draft: <strong>a refused save leaves the stored credentials exactly as
/// they were</strong>.</para>
///
/// <para><strong>The status read deliberately carries no plan requirement</strong> and stays
/// available on every plan, to every role that can see it today. It makes no Nayax call, and it is
/// what tells a business that was downgraded that its saved credentials and their status are still
/// there - withholding it would make a kept connection look lost.</para>
/// </summary>
[ApiController]
[Route("api/settings/nayax-connection")]
[Authorize]
[RequiredScope("access_as_user")]
public sealed class NayaxConnectionController : ControllerBase
{
    private readonly TestNayaxConnection _test;
    private readonly SaveNayaxConnection _save;
    private readonly RetestNayaxConnection _retest;
    private readonly GetNayaxConnectionStatus _status;

    public NayaxConnectionController(
        TestNayaxConnection test,
        SaveNayaxConnection save,
        RetestNayaxConnection retest,
        GetNayaxConnectionStatus status)
    {
        _test = test;
        _save = save;
        _retest = retest;
        _status = status;
    }

    /// <summary>
    /// Runs the read-only Nayax checks with the submitted operator id and token, and holds them as
    /// this business's one short-lived encrypted draft.
    ///
    /// Nothing is accepted by testing: the business's existing connection, its status and its
    /// last-tested instant are untouched, and no Nayax data is written. The returned
    /// <c>draftId</c> is what <see cref="Save"/> names.
    /// </summary>
    /// <response code="200">The checks ran. Individual lines may still report a failure.</response>
    /// <response code="400">The operator id or the token was missing.</response>
    /// <response code="429">
    /// This business has run too many tests too recently. <c>Retry-After</c> carries the wait, in
    /// seconds.
    /// </response>
    [HttpPost("test")]
    [RequireCapability(Capability.IntegrationManage)]
    [RequirePlan(PlanFeature.SalesProviderIntegration)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<NayaxConnectionTestResponse>> Test(
        [FromBody] NayaxConnectionTestRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var outcome = await _test.Handle(request.OperatorId, request.Token, cancellationToken);

        return outcome.RetryAfter is { } retryAfter
            ? TooManyTests(retryAfter)
            : Ok(NayaxConnectionTestResponse.From(outcome.Result!));
    }

    /// <summary>
    /// Promotes one completed test into this business's Nayax connection, and deletes the draft it
    /// came from.
    ///
    /// The request carries the draft id and never a token, so the credentials stored are exactly the
    /// ones the server tested. The status comes from that draft's own results.
    /// </summary>
    /// <response code="200">The credentials were stored; the body reports the resulting state.</response>
    /// <response code="400">
    /// No draft id was submitted, or the tested token did not sign in to Nayax, so nothing was
    /// stored.
    /// </response>
    /// <response code="409">
    /// There was no such draft to claim: it was already saved, has expired, or belongs to another
    /// business or another member.
    /// </response>
    [HttpPost("save")]
    [RequireCapability(Capability.IntegrationManage)]
    [RequirePlan(PlanFeature.SalesProviderIntegration)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<NayaxConnectionStatusResponse>> Save(
        [FromBody] NayaxConnectionSaveRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The refusals are DomainValidationException and DomainConflictException, which
        // DomainExceptionHandler maps to 400 and 409 with their own caller-safe sentences.
        return Ok(NayaxConnectionStatusResponse.From(
            await _save.Handle(request.DraftId, cancellationToken)));
    }

    /// <summary>
    /// Runs the same read-only checks again with the token this business already has stored, and
    /// writes what they concluded.
    ///
    /// It works in every status, including <c>NeedsAttention</c>, which is the state it exists to
    /// recover from. A run Nayax never answered leaves the stored status exactly as it was and says
    /// so through <c>statusUpdated</c>.
    /// </summary>
    /// <response code="200">The checks ran; the body reports the connection and every line.</response>
    /// <response code="409">
    /// This business has stored no Nayax credentials to re-test, or they were replaced while the
    /// re-test ran, so its result was discarded.
    /// </response>
    /// <response code="429">
    /// This business has run too many tests too recently - test and re-test runs share one
    /// allowance. <c>Retry-After</c> carries the wait, in seconds.
    /// </response>
    [HttpPost("retest")]
    [RequireCapability(Capability.IntegrationManage)]
    [RequirePlan(PlanFeature.SalesProviderIntegration)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<NayaxConnectionRetestResponse>> Retest(CancellationToken cancellationToken)
    {
        var outcome = await _retest.Handle(cancellationToken);

        return outcome.RetryAfter is { } retryAfter
            ? TooManyTests(retryAfter)
            : Ok(NayaxConnectionRetestResponse.From(outcome.Result!));
    }

    /// <summary>
    /// Reports whether Nayax is working for this business, for any of its members.
    ///
    /// It answers with a status and whether a re-test is still needed to verify the machine checks,
    /// and with nothing else - no operator id, no credential revision, no last-tested instant and no
    /// check lines.
    /// </summary>
    /// <response code="200">The business's connection state.</response>
    [HttpGet("status")]
    [RequireCapability(Capability.DashboardView)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<NayaxConnectionStatusResponse>> Status(CancellationToken cancellationToken) =>
        Ok(NayaxConnectionStatusResponse.From(await _status.Handle(cancellationToken)));

    /// <summary>
    /// The shared refusal of the per-business run limit, which test and re-test spend together.
    ///
    /// A fixed sentence with no count, no window and no business in it: how often this business has
    /// tested is not something a response needs to spell out.
    /// </summary>
    private ActionResult TooManyTests(TimeSpan retryAfter)
    {
        // Rounded up, so a client that waits exactly this long is past the window rather than one
        // tick short of it, and never below one second.
        var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
        Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);

        return StatusCode(
            StatusCodes.Status429TooManyRequests,
            new ProblemDetails
            {
                Title = "Too many Nayax connection tests",
                Detail = "This business has tested its Nayax connection too many times recently. "
                    + "Wait a few minutes and try again.",
                Status = StatusCodes.Status429TooManyRequests,
            });
    }
}
