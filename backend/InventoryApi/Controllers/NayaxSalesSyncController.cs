using System.Globalization;
using Inventory.Application.Access;
using Inventory.Application.SalesSync;
using InventoryApi.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

// The explicit, shared latest-Nayax-sales synchronization endpoint (issue #187). The home
// dashboard calls this once before loading Sites and Machines so both sections calculate their
// figures from the same freshness boundary, instead of Machines importing the latest transactions
// as a side effect of MachineService.GetAll(). The controller is a thin adapter over the
// Inventory.Application.SalesSync.SyncLatestNayaxSales use case: it binds nothing but the request's
// cancellation token and maps the use case's outcome to 204 or 429.
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

    private readonly SyncLatestNayaxSales _syncLatestSales;

    public NayaxSalesSyncController(SyncLatestNayaxSales syncLatestSales)
    {
        _syncLatestSales = syncLatestSales;
    }

    /// <summary>
    /// Reads this business's latest Nayax sales and persists the batch.
    /// </summary>
    /// <response code="204">The synchronization ran.</response>
    /// <response code="429">
    /// This business has synchronized too many times too recently (issue #681). <c>Retry-After</c>
    /// carries the wait, in seconds. The Dashboard shows its persisted figures flagged as not
    /// synchronized, the same state it shows when Nayax cannot be reached.
    /// </response>
    [HttpPost]
    [RequireCapability(Capability.InventoryOperate)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> SyncLatest(CancellationToken ct)
    {
        var outcome = await _syncLatestSales.Handle(ct);

        return outcome.RetryAfter is { } retryAfter ? TooManySyncs(retryAfter) : NoContent();
    }

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
