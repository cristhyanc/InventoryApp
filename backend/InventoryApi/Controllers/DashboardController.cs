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
/// landing page of the application. <c>Dashboard.ViewFinancials</c> is not an endpoint requirement
/// here and deliberately cannot be: what a role without it may not see is part of the
/// <em>response</em>, not the endpoint, so the use case decides the shape and this controller
/// returns whichever one it built (issue #527). It strips, recomputes and re-decides nothing.
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
    /// The declared type is the published union of the two shapes
    /// (<c>DashboardSummaryDto</c> for a caller with <c>Dashboard.ViewFinancials</c>,
    /// <c>RedactedDashboardSummaryDto</c> for one without it); the response is serialized from the
    /// one the use case actually returned, so a redacted property is absent from the JSON rather
    /// than written as <c>null</c>.
    /// </summary>
    [HttpGet("summary")]
    public Task<IDashboardSummaryDto> Summary(CancellationToken cancellationToken) =>
        _getDashboardSummary.Handle(cancellationToken);
}
