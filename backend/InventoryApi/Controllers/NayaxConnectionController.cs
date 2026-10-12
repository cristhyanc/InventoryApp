using System.Globalization;
using Inventory.Application.Access;
using Inventory.Application.Nayax;
using InventoryApi.Auth;
using InventoryApi.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

/// <summary>
/// The HTTP surface of the business's own Nayax connection (issue #536, a slice of #506 under
/// #328). Today it holds one action: testing a pasted operator id and token.
///
/// <para>The controller is thin, as every controller here is: it binds the request, invokes the one
/// use case with the request's cancellation token, and maps the outcome to HTTP. No Nayax call,
/// status mapping, encryption, expiry rule, rate-limit decision or EF query lives here. Ownership is
/// the central one - <c>AppDbContext</c>'s query filters and its ownership stamp on save - so no
/// route, body or header value can name an owner, and a stored draft belongs to the business and the
/// Owner that created it.</para>
///
/// <para>It requires <c>Integration.Manage</c> (issues #525 and #526), which #328's table grants to
/// an Owner alone: this action submits credentials for the whole business's Nayax integration and
/// holds them, encrypted, on the server. It sits beside <see cref="SettingsController"/>'s
/// processing-fee rates, which carries the same capability for the same reason. A denied call
/// returns <c>403</c> with no detail about the capability or the caller's role.</para>
///
/// <para>The action is a <c>POST</c>, and not only because it writes the draft: it submits a secret
/// in a body rather than a URL, it calls out to Nayax, and a cached <c>GET</c> response would hand a
/// caller a verdict about credentials it never tested.</para>
/// </summary>
[ApiController]
[Route("api/settings/nayax-connection")]
[Authorize]
[RequiredScope("access_as_user")]
[RequireCapability(Capability.IntegrationManage)]
public sealed class NayaxConnectionController : ControllerBase
{
    private readonly TestNayaxConnection _test;

    public NayaxConnectionController(TestNayaxConnection test)
    {
        _test = test;
    }

    /// <summary>
    /// Runs the read-only Nayax checks with the submitted operator id and token, and holds them as
    /// this business's one short-lived encrypted draft.
    ///
    /// Nothing is accepted by testing: the business's existing connection, its status and its
    /// last-tested instant are untouched, and no Nayax data is written. The returned
    /// <c>draftId</c> is what the separate save step (#537) names.
    /// </summary>
    /// <response code="200">The checks ran. Individual lines may still report a failure.</response>
    /// <response code="400">The operator id or the token was missing.</response>
    /// <response code="429">
    /// This business has run too many tests too recently. <c>Retry-After</c> carries the wait, in
    /// seconds.
    /// </response>
    [HttpPost("test")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<NayaxConnectionTestResponse>> Test(
        [FromBody] NayaxConnectionTestRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var outcome = await _test.Handle(request.OperatorId, request.Token, cancellationToken);

        if (outcome.RetryAfter is { } retryAfter)
        {
            // Rounded up, so a client that waits exactly this long is past the window rather than
            // one tick short of it, and never below one second.
            var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
            Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);

            // A fixed sentence with no count, no window and no business in it: how often this
            // business has tested is not something a response needs to spell out.
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

        return Ok(NayaxConnectionTestResponse.From(outcome.Result!));
    }
}
