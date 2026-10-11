using Inventory.Application.Access;
using Inventory.Application.Dashboard;
using InventoryApi.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

/// <summary>
/// The home Dashboard's authoritative summary endpoint (issue #459). It is a thin HTTP boundary: it
/// binds no input, invokes <see cref="GetDashboardSummary"/> and returns its result. The request's
/// business is resolved from the actor's membership and every read is tenant-scoped centrally; this
/// endpoint takes no business, machine or site identifier from the caller.
///
/// It requires <c>Dashboard.View</c> (issue #525), which every role holds: the Dashboard is the
/// landing page of the application. Which <em>figures</em> the summary carries is a second decision,
/// made on <c>Dashboard.ViewFinancials</c> inside the use case (issue #527), because what is
/// restricted here is part of a response rather than a whole endpoint. The controller stays thin:
/// it returns whichever of the two published shapes the use case built and never decides, recomputes
/// or strips a figure itself.
/// </summary>
[ApiController]
[Route("api/dashboard")]
[Authorize]
[RequiredScope("access_as_user")]
[RequireCapability(Capability.DashboardView)]
public sealed class DashboardController : ControllerBase
{
    private readonly GetDashboardSummary _getDashboardSummary;

    public DashboardController(GetDashboardSummary getDashboardSummary)
    {
        _getDashboardSummary = getDashboardSummary;
    }

    /// <summary>
    /// The read-only sales/refill/ordering/inventory summary behind the Dashboard cards. It creates
    /// no inventory movement, refill or persisted state, and adds no figure a caller must calculate
    /// itself.
    ///
    /// <para>The response is <see cref="DashboardSummaryDto"/> for a caller holding
    /// <c>Dashboard.ViewFinancials</c> and <see cref="RedactedDashboardSummaryDto"/> for one who
    /// does not, which omits <c>salesThisWeek</c> and <c>inventory.inventoryValueAtCost</c> entirely
    /// (issue #527). The documented shape is the full one; a client reads a financial figure only
    /// when the response carries it, and <c>GET /api/me/access</c> is how it knows in advance.</para>
    /// </summary>
    [HttpGet("summary")]
    [ProducesResponseType<DashboardSummaryDto>(StatusCodes.Status200OK)]
    public Task<IDashboardSummaryDto> Summary(CancellationToken cancellationToken) =>
        _getDashboardSummary.Handle(cancellationToken);
}
