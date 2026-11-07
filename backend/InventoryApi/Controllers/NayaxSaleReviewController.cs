using Inventory.Application.Access;
using Inventory.Application.NayaxSaleReview;
using Inventory.Domain.NayaxSqs;
using InventoryApi.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

/// <summary>
/// The HTTP surface of the Nayax sales review (issue #1105): the list and the Dashboard
/// needs-attention summary, which every member with <c>Dashboard.View</c> may read, and the business
/// Owner's decision, behind <c>Data.Repair</c> (Owner-only). A read-only support session and a role
/// preview are refused the decision by their own method rules.
///
/// <para>An ordinary tenant-scoped endpoint family: no route, body or header names a business, and
/// a sale or evidence id of another business is simply not found. Nothing is caught here: a stale
/// state, stale evidence or an unlinked Nayax product is a <c>DomainConflictException</c> mapped
/// centrally to <c>409</c>, and a decision the table refuses or a missing reason is a
/// <c>DomainValidationException</c> mapped to <c>400</c>.</para>
/// </summary>
[ApiController]
[Route("api/nayax-sale-review")]
[Authorize]
[RequiredScope("access_as_user")]
public sealed class NayaxSaleReviewController : ControllerBase
{
    private readonly ReviewNayaxSales _review;

    public NayaxSaleReviewController(ReviewNayaxSales review)
    {
        _review = review;
    }

    /// <summary>
    /// One page of the business's flagged (<c>view=10</c>, the default) or Owner-decided
    /// (<c>view=20</c>) queue sales, newest first, with the view's total.
    /// </summary>
    /// <response code="200">The page.</response>
    /// <response code="400">The page index is negative, or the page size is outside 1 to 100.</response>
    [HttpGet]
    [RequireCapability(Capability.DashboardView)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<NayaxSaleReviewPage>> List(
        [FromQuery] NayaxSaleReviewView view = NayaxSaleReviewView.NeedsReview,
        [FromQuery] int pageIndex = 0,
        [FromQuery] int pageSize = NayaxSaleReviewQuery.DefaultPageSize,
        CancellationToken cancellationToken = default) =>
        Ok(await _review.ListAsync(new NayaxSaleReviewQuery(view, pageIndex, pageSize), cancellationToken));

    /// <summary>How many sales wait for the Owner, and how many of those are not being counted.</summary>
    [HttpGet("summary")]
    [RequireCapability(Capability.DashboardView)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<NayaxSaleReviewSummary>> Summary(CancellationToken cancellationToken) =>
        Ok(await _review.SummaryAsync(cancellationToken));

    /// <summary>
    /// Applies the Owner's decision about one sale, presenting the state and latest evidence id the
    /// page showed and the evidence the decision relies on.
    /// </summary>
    /// <response code="200">The decision was applied.</response>
    /// <response code="400">The decision is not allowed, or the reason is missing or too long.</response>
    /// <response code="404">No such queue sale in this business.</response>
    /// <response code="409">
    /// The sale or its evidence changed since the page read it, or Nayax's product is not linked to a
    /// product in this business. Nothing was written.
    /// </response>
    [HttpPost("{saleId:long}/decision")]
    [RequireCapability(Capability.DataRepair)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<NayaxSaleDecisionApplied>> Decide(
        long saleId, NayaxSaleDecisionBody body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);

        var applied = await _review.DecideAsync(
            new NayaxSaleDecisionRequest(
                saleId, body.PresentedState, body.PresentedLatestEvidenceId, body.EvidenceId, body.Decision, body.Reason),
            cancellationToken);

        return applied is null ? NotFound() : Ok(applied);
    }
}

/// <summary>The body of a decision request.</summary>
public sealed record NayaxSaleDecisionBody(
    NayaxSqsVerification PresentedState,
    long PresentedLatestEvidenceId,
    long EvidenceId,
    NayaxSaleOwnerDecision Decision,
    string? Reason);
