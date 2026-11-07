using System.Globalization;
using Inventory.Application.Access;
using Inventory.Application.SalesSync;
using InventoryApi.Auth;
using InventoryApi.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

// The explicit, shared latest-Nayax-sales synchronization endpoint (issue #187). The home
// dashboard calls this once before loading Sites and Machines so both sections calculate their
// figures from the same freshness boundary, instead of Machines importing the latest transactions
// as a side effect of MachineService.GetAll(). The controller is a thin adapter over the
// Inventory.Application.SalesSync.SyncLatestNayaxSales use case: it binds nothing but the request's
// cancellation token and the `force` flag the Dashboard's Refresh control sets (issue #843), and
// maps the use case's outcome to 204, 200, 409 or 429. Whether this business's sales arrive live
// from SQS (issue #1106) is the use case's and the Domain rule's decision too; the controller only
// publishes it.
//
// It requires Dashboard.View (issue #526), because issue #526 says this endpoint follows the page
// that calls it, and that page is the home Dashboard: putting it on Imports.Run instead would leave
// an Operator's Dashboard refreshing nothing and reporting stale sales on every visit - the page
// broken for them rather than a decision anybody made. It surfaces no figure the Dashboard does not
// already show, and redacting those figures by role is issue #527.
//
// Since issue #681 it *additionally* requires Inventory.Operate, and that is the point: this
// endpoint writes. It persists imported sales and rebuilds inventory costs, and Dashboard.View is a
// read capability - the one every role holds, including any future read-only role #328 anticipates
// (the Viewer). An endpoint that writes under a read-only capability is a hole waiting for that
// role to exist, so it declares a capability that permits writes as well. Inventory.Operate is the
// one it declares because the write is an inventory one (the same capability Sync Restock's write
// carries) and because every role in #502's matrix that holds Dashboard.View also holds it: no
// current role's sync access changes. Capability requirements accumulate down an endpoint, so this
// narrows and never widens.
[ApiController]
[Route("api/nayax-sales-sync")]
[Authorize]
[RequiredScope("access_as_user")]
[RequireCapability(Capability.DashboardView)]
public class NayaxSalesSyncController : ControllerBase
{
    internal const string TooManySyncsTitle = "Too many Nayax sales refreshes";

    /// <summary>
    /// The title of the <c>409</c> a request gets when a synchronization for its business is already
    /// running (issue #1104).
    /// </summary>
    internal const string SyncAlreadyRunningTitle = "A Nayax sales sync is already running";

    /// <summary>
    /// The <c>409</c>'s sentence. It names no business, no run, no wait and no other member: that a
    /// background reconciliation or somebody else's refresh is in progress is not something this
    /// response needs to spell out, and "try again shortly" is the whole of what the caller can do.
    /// </summary>
    internal const string SyncAlreadyRunningDetail =
        "A sync for this business is already running. Try again shortly.";

    private readonly SyncLatestNayaxSales _syncLatestSales;

    public NayaxSalesSyncController(SyncLatestNayaxSales syncLatestSales)
    {
        _syncLatestSales = syncLatestSales;
    }

    /// <summary>
    /// Reads this business's latest Nayax sales and persists the batch, unless they were
    /// synchronized moments ago.
    /// </summary>
    /// <param name="force">
    /// The Dashboard's Refresh control (issue #843): <c>true</c> synchronizes even when the last run
    /// finished moments ago. It is not a way past the run limit or the connection refusals, and it is
    /// the only thing this request carries - never a business, a machine or an instant.
    /// </param>
    /// <param name="ct">The request's cancellation token.</param>
    /// <response code="204">
    /// The synchronization ran. It carries no body, so a business that is not live via SQS and did
    /// run learns the not-live reason from its next skipped call rather than from here - which is
    /// exactly how this answer behaved before issue #1106.
    /// </response>
    /// <response code="200">
    /// Nothing was read or written, because there was nothing to read: this business's sales were
    /// already synchronized less than three minutes ago (issue #843), or they arrive live from the
    /// shared Nayax SQS queue (issue #1106). The body says which, when they were last synchronized,
    /// when the queue last delivered, and - when the business is not live - why.
    /// </response>
    /// <response code="409">
    /// A synchronization for this business is already running - the hourly reconciliation of issue
    /// #1104, or another member's refresh - and this request waited a minute for it without getting
    /// in. Nothing was read from Nayax, nothing was written, no coverage was recorded and no run
    /// allowance was claimed, so the attempt does not count against issue #681's limit. The Dashboard
    /// treats it as a skip: the figures it loads next are the ones the run in progress is producing.
    /// </response>
    /// <response code="429">
    /// This business has synchronized too many times too recently (issue #681). <c>Retry-After</c>
    /// carries the wait, in seconds. The Dashboard shows its persisted figures flagged as not
    /// synchronized, the same state it shows when Nayax cannot be reached.
    /// </response>
    [HttpPost]
    [RequireCapability(Capability.InventoryOperate)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(NayaxSalesRecentlySyncedResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> SyncLatest([FromQuery] bool force, CancellationToken ct)
    {
        var outcome = await _syncLatestSales.Handle(force, ct);

        if (outcome.RetryAfter is { } retryAfter)
        {
            return TooManySyncs(retryAfter);
        }

        if (outcome.IsBusy)
        {
            // Its own status, kept off 204, 200 and 429 so each of those keeps exactly the meaning
            // every existing caller already has. It carries no Retry-After: this is not a rationed
            // request and the wait is however long the run in progress still needs.
            return SyncAlreadyRunning();
        }

        // A skip is its own answer, kept off 204 and 429 so both of those keep exactly the meaning
        // every existing caller already has. Since issue #1106 there are two of them - the sales
        // were read moments ago, or they arrive live from SQS - and the body tells them apart.
        return outcome.RecentlySyncedAtUtc is not null || outcome.LiveViaSqs
            ? Ok(NayaxSalesRecentlySyncedResponse.From(outcome))
            : NoContent();
    }

    /// <summary>
    /// The already-running refusal (issue #1104). A fixed sentence with no business, no count and no
    /// wait in it, exactly as the run limit's is.
    /// </summary>
    private ActionResult SyncAlreadyRunning() =>
        Conflict(new ProblemDetails
        {
            Title = SyncAlreadyRunningTitle,
            Detail = SyncAlreadyRunningDetail,
            Status = StatusCodes.Status409Conflict,
        });

    /// <summary>
    /// The per-business run limit's refusal. A fixed sentence with no count, no window and no
    /// business in it, exactly as the Nayax connection test's is: how often this business has
    /// refreshed is not something a response needs to spell out. It names no wait either, and points
    /// at <c>Retry-After</c> instead, because the wait is anything up to the whole window and a
    /// sentence promising "a minute" would be wrong for most of it.
    /// </summary>
    private ActionResult TooManySyncs(TimeSpan retryAfter)
    {
        // Rounded up, so a client that waits exactly this long is past the window rather than one
        // tick short of it, and never below one second.
        var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
        Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);

        return StatusCode(
            StatusCodes.Status429TooManyRequests,
            new ProblemDetails
            {
                Title = TooManySyncsTitle,
                Detail = "This business has refreshed its Nayax sales too many times recently. "
                    + "Try again once the time given in the Retry-After header has passed.",
                Status = StatusCodes.Status429TooManyRequests,
            });
    }
}
