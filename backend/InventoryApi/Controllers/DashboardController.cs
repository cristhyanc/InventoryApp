using Inventory.Application.Dashboard;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

/// <summary>
/// The home Dashboard's authoritative summary endpoint (issue #459). It is a thin HTTP boundary: it
/// binds no input, invokes <see cref="GetDashboardSummary"/> and returns its result. Authorization is
/// the ordinary authenticated-user surface every other business endpoint carries, so the request's
/// business is resolved from the actor's membership and every read is tenant-scoped centrally; this
/// endpoint takes no business, machine or site identifier from the caller.
/// </summary>
[ApiController]
[Route("api/dashboard")]
[Authorize]
[RequiredScope("access_as_user")]
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
    /// </summary>
    [HttpGet("summary")]
    public Task<DashboardSummaryDto> Summary(CancellationToken cancellationToken) =>
        _getDashboardSummary.Handle(cancellationToken);
}
