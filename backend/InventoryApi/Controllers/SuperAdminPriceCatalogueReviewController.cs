using Inventory.Application.PriceCatalogue;
using Inventory.Domain.PriceCatalogue;
using InventoryApi.Auth.PlatformAdmin;
using InventoryApi.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

/// <summary>
/// The Super Admin's review of the shared supplier price catalogue (issue #928, parent #817): the
/// products awaiting a match, the matches a collector run applied by itself, and the supplier
/// listings waiting for a decision - with the one authority that may confirm, correct or refuse any
/// of them.
///
/// <para><strong>Only a Super Admin reaches it, and that is the owner's decision of 27 October
/// 2026.</strong> A supplier listing, a pack quantity and a master product are shared by every
/// business, so one wrong pack count is one wrong "cheapest supplier" for everybody tracking the
/// product. Claude may propose - the collector's results (#937) hold a reading rather than price it,
/// and its matcher (#941) may link a waiting key to an <em>existing</em> master product under the
/// backend's own checks - and a Business Owner may not confirm anything here at all, because the data
/// is not theirs.</para>
///
/// <para><strong>Authorisation is the other Super Admin surfaces', unchanged.</strong>
/// <c>[Authorize(Policy = PlatformAdminPolicy.Name)]</c> is evaluated by the authorization middleware
/// before any action here runs, and <c>BusinessScopeMiddleware</c> re-evaluates the same policy before
/// it lets the request past the membership requirement. That is class 2 of the five ways an endpoint
/// may declare its authorization (<c>EndpointAuthorizationClassificationTests</c>) - one class, one
/// marker, one policy, and no sixth - and both halves are required: the marker alone grants nothing
/// and the policy alone would not be eligible for the membership bypass. There is no token claim,
/// header, route value or body field that selects the authority: it is the configured Entra
/// <c>(tid, oid)</c> pair and nothing else, and both values empty - the shipped state - means nobody
/// can reach this controller at all. An Owner of a business is refused exactly as any other member
/// is, a role preview is refused on every write by <c>RolePreviewMiddleware</c>, and a member
/// presenting a support-session id is still an ordinary member because this endpoint class never
/// resolves a session.</para>
///
/// <para><strong>The request carries a denied <c>BusinessScope</c>, and the one tenant-owned thing it
/// may write is four columns of a price-tracking mapping</strong> - the mappings a match or an undo
/// settles, through the narrow review window of <c>AppDbContext.BeginPriceCatalogueReview</c>. No
/// business id, business name or product id of anybody's appears in any request or response in either
/// direction, and a waiting product is published by its search name, its unit label and a
/// <em>count</em> of businesses waiting. See docs/architecture.md § Super Admin review of the
/// supplier price catalogue.</para>
///
/// <para>The controller is thin, as every controller here is: it binds the route value and the body,
/// invokes a use case with the request's cancellation token, and maps the outcome to HTTP. Every rule
/// is the Domain's, and the refusals that carry a sentence are <c>DomainValidationException</c> and
/// <c>DomainConflictException</c>, which <c>DomainExceptionHandler</c> answers as <c>400</c> and
/// <c>409</c> with the Domain's own caller-safe messages.</para>
/// </summary>
[ApiController]
[Route("api/super-admin/price-catalogue-review")]
// Both, and both are enforced: the default policy carries the delegated-scope requirement that
// [RequiredScope] declares, and the named policy carries the platform-admin one. A named policy
// alone would replace the default rather than add to it, which would leave the scope undemanded.
[Authorize]
[Authorize(Policy = PlatformAdminPolicy.Name)]
[RequiredScope("access_as_user")]
[PlatformDiagnosticsEndpoint]
public sealed class SuperAdminPriceCatalogueReviewController : ControllerBase
{
    private readonly GetPriceCatalogueReviewQueue _queue;
    private readonly DecideSupplierListingReview _decideListing;
    private readonly DecideProductMatchReview _decideMatch;
    private readonly DecideAutomaticProductMatchReview _decideAutomaticMatch;

    public SuperAdminPriceCatalogueReviewController(
        GetPriceCatalogueReviewQueue queue,
        DecideSupplierListingReview decideListing,
        DecideProductMatchReview decideMatch,
        DecideAutomaticProductMatchReview decideAutomaticMatch)
    {
        _queue = queue;
        _decideListing = decideListing;
        _decideMatch = decideMatch;
        _decideAutomaticMatch = decideAutomaticMatch;
    }

    /// <summary>
    /// The whole review queue: products awaiting a match, automatic matches waiting to be checked,
    /// and listings waiting for a decision - the last of them with barcode matches first.
    ///
    /// <para>It takes no filter, page, sort or search parameter, for the reason the supplier presets
    /// and the business directory take none: a caller-supplied predicate is how a fixed platform list
    /// becomes a query engine.</para>
    /// </summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<SuperAdminPriceCatalogueReviewResponse>> Queue(
        CancellationToken cancellationToken)
    {
        var queue = await _queue.Handle(cancellationToken);

        return Ok(SuperAdminPriceCatalogueReviewResponse.From(queue));
    }

    /// <summary>
    /// Decides one supplier listing: confirm it with the pack quantity, barcode scope and identity as
    /// reviewed - which is also how a correction is made - or refuse it.
    ///
    /// <para>Confirming also accepts the reading the listing was holding as its price, but only while
    /// that reading is inside the supplier's freshness limit, describes the pack that was confirmed
    /// and is inside the band around the last accepted price. Otherwise the listing is confirmed with
    /// no current price and is priced on the next run, and the response says which of those happened
    /// and why.</para>
    /// </summary>
    /// <response code="200">What the decision did, including what became of the pending reading.</response>
    /// <response code="400">
    /// The outcome is not a declared one, or the submitted identity is not one this catalogue stores -
    /// a pack quantity that is not positive, a barcode without its scope, a size without its unit.
    /// Nothing was written.
    /// </response>
    /// <response code="404">No such listing.</response>
    /// <response code="409">
    /// The listing is no longer waiting for a decision: another reviewer or a collector run has moved
    /// it since this page was read. Nothing was written.
    /// </response>
    [HttpPost("listings/{supplierListingId:long}/decision")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<SuperAdminSupplierListingDecisionResponse>> DecideListing(
        long supplierListingId,
        SuperAdminSupplierListingDecisionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await _decideListing.Handle(
            request.ToSubmission(supplierListingId),
            cancellationToken);

        return result is null
            ? NotFound()
            : Ok(SuperAdminSupplierListingDecisionResponse.From(result));
    }

    /// <summary>
    /// Decides one waiting product: accept the matcher's suggestion, pick another existing master
    /// product, create one, refuse a suggestion, or record that nothing in the catalogue is this
    /// product.
    ///
    /// <para>A match settles <strong>every</strong> mapping waiting under that key, in every business
    /// that is waiting, and moves the listings the key had collected to the master product - still as
    /// proposals, because a match says which product a search was for and never that somebody
    /// confirmed a listing.</para>
    /// </summary>
    /// <response code="200">What the decision did.</response>
    /// <response code="400">
    /// The decision is not a declared one, its own values are incomplete, or the master product named
    /// or described is not one this catalogue stores. Nothing was written.
    /// </response>
    /// <response code="404">
    /// Nothing is awaiting a match under that waiting key: it has been matched, recorded as not found
    /// or withdrawn since this page was read.
    /// </response>
    /// <response code="409">
    /// The suggestion named is no longer waiting for a decision, or another master product already
    /// describes what a create submitted. Nothing was written.
    /// </response>
    [HttpPost("matches")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<SuperAdminProductMatchDecisionResponse>> DecideMatch(
        SuperAdminProductMatchDecisionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await _decideMatch.Handle(request.ToSubmission(), cancellationToken);

        return result is null
            ? NotFound()
            : Ok(SuperAdminProductMatchDecisionResponse.From(result));
    }

    /// <summary>
    /// Confirms one automatic match (#941): the mappings it applied to are settled and the match is
    /// recorded as accepted.
    /// </summary>
    /// <response code="200">What the decision did.</response>
    /// <response code="404">No such match suggestion.</response>
    /// <response code="409">
    /// It is not an automatic match waiting to be checked: it has been confirmed or undone since.
    /// </response>
    [HttpPost("automatic-matches/{suggestionId:long}/confirm")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<ActionResult<SuperAdminAutomaticMatchDecisionResponse>> ConfirmAutomaticMatch(
        long suggestionId,
        CancellationToken cancellationToken) =>
        DecideAutomaticMatch(
            suggestionId,
            AutomaticMatchReviewDecision.Confirm,
            cancellationToken);

    /// <summary>
    /// Undoes one automatic match (#941): the mappings return to awaiting a match, the proposed
    /// listings that came from that key move back to it, confirmed listings are untouched, and the
    /// pair is recorded as undone so it is never applied automatically again.
    ///
    /// <para>It is a separate route from the confirmation rather than a decision in a body, because
    /// the two are different actions on the same row and an undeclared enum value in a body is a
    /// value this surface would then have to refuse.</para>
    /// </summary>
    /// <response code="200">What the undo did, including how many listings moved back.</response>
    /// <response code="404">No such match suggestion.</response>
    /// <response code="409">
    /// It is not an automatic match waiting to be checked: it has been confirmed or undone since.
    /// </response>
    [HttpPost("automatic-matches/{suggestionId:long}/undo")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<ActionResult<SuperAdminAutomaticMatchDecisionResponse>> UndoAutomaticMatch(
        long suggestionId,
        CancellationToken cancellationToken) =>
        DecideAutomaticMatch(suggestionId, AutomaticMatchReviewDecision.Undo, cancellationToken);

    private async Task<ActionResult<SuperAdminAutomaticMatchDecisionResponse>> DecideAutomaticMatch(
        long suggestionId,
        AutomaticMatchReviewDecision decision,
        CancellationToken cancellationToken)
    {
        var result = await _decideAutomaticMatch.Handle(suggestionId, decision, cancellationToken);

        return result is null
            ? NotFound()
            : Ok(SuperAdminAutomaticMatchDecisionResponse.From(result));
    }
}
