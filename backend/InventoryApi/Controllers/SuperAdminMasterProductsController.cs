using Inventory.Application.PriceCatalogue;
using InventoryApi.Auth.PlatformAdmin;
using InventoryApi.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

/// <summary>
/// The Super Admin's management of the shared supplier price catalogue's master products (issues
/// #1023 and #1024, parent #817): every product the catalogue holds, searchable and one bounded page
/// at a time, one product with the supplier listings that hang from it, and - since #1024 - adding one
/// and correcting one.
///
/// <para><strong>This controller and the Supplier Price Review of issue #928 are the two writers of
/// <c>MasterProducts</c>, and there is no third.</strong> The review creates one as part of deciding
/// which product a business's search was for; this one keeps the catalogue in its own right, which is
/// how a product is added before any business asks for it and how a wrong brand, name or size is
/// corrected. Both are class 2 platform-admin, both run under a denied <c>BusinessScope</c>, and no
/// automatic match ever creates a master product. <strong>Deleting</strong> one is a separate issue
/// that follows this.</para>
///
/// <para><strong>A write here changes the shared catalogue and nothing else.</strong> It never
/// creates, confirms or refuses a supplier listing, never matches or unmatches a business's tracked
/// product, and never reads or writes a business's own product, stock, cost, purchase or price-tracking
/// row - the owner's clarification of 31 October 2026, <em>catalogue-only writes</em>. A brand or name
/// edit changes the shared catalogue's label, not anybody's product record.</para>
///
/// <para><strong>The size is the one value this surface will refuse to change.</strong> While any
/// supplier listing of the product is <c>Confirmed</c>, a change to the size value or unit is
/// <c>409</c>: those listings' pack sizes were confirmed against the current size, and every tracking
/// business's per-unit price would change without anybody confirming anything. Brand and name edits
/// are always allowed.</para>
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
/// can reach this controller at all. An Owner of a business is refused exactly as any other member is,
/// a role preview is refused on every write by <c>RolePreviewMiddleware</c>, and a member presenting a
/// support-session id is still an ordinary member, because this endpoint class never resolves a
/// session.</para>
///
/// <para><strong>The request carries a denied <c>BusinessScope</c> and nothing tenant-owned is read or
/// written at all.</strong> Every table behind these four endpoints is a platform table about a
/// product or a supplier's shelf: no business id, business name or business product id appears in any
/// request or response, no price-tracking mapping is read, and no count of the businesses tracking a
/// product is offered - master-catalogue management only, which is the owner's clarification of
/// 31 October 2026. The write needs no reviewed denied-scope lease for the same reason: a master
/// product is not <c>IBusinessOwned</c>, so <c>BusinessOwnershipEnforcer</c> is fully in force over
/// everything else. See <c>Inventory.Infrastructure.Persistence.EfMasterProductCatalogueStore</c>,
/// <c>EfMasterProductCatalogueWriteStore</c> and docs/architecture.md § Super Admin master
/// products.</para>
///
/// <para>The controller is thin, as every controller here is: it binds the query string, the route
/// value and the body, invokes a use case with the request's cancellation token, and maps the answer to
/// HTTP. The page bound is <c>MasterProductCataloguePaging</c>'s, the order and the search are the
/// store's, and every rule a write is held to is the Domain's - the refusals that carry a sentence are
/// <c>DomainValidationException</c> and <c>DomainConflictException</c>, which
/// <c>DomainExceptionHandler</c> answers as <c>400</c> and <c>409</c> with the Domain's own
/// caller-safe messages.</para>
/// </summary>
[ApiController]
[Route("api/super-admin/master-products")]
// Both, and both are enforced: the default policy carries the delegated-scope requirement that
// [RequiredScope] declares, and the named policy carries the platform-admin one. A named policy
// alone would replace the default rather than add to it, which would leave the scope undemanded.
[Authorize]
[Authorize(Policy = PlatformAdminPolicy.Name)]
[RequiredScope("access_as_user")]
[PlatformDiagnosticsEndpoint]
public sealed class SuperAdminMasterProductsController : ControllerBase
{
    private readonly ListMasterProducts _list;
    private readonly GetMasterProduct _get;
    private readonly CreateMasterProduct _create;
    private readonly UpdateMasterProduct _update;

    public SuperAdminMasterProductsController(
        ListMasterProducts list,
        GetMasterProduct get,
        CreateMasterProduct create,
        UpdateMasterProduct update)
    {
        _list = list;
        _get = get;
        _create = create;
        _update = update;
    }

    /// <summary>
    /// One page of the shared catalogue, ordered by brand, name, size and then the row's own key, with
    /// the number of products the search matched in total.
    ///
    /// <para><c>search</c> is optional and matches the brand or the name, ignoring case; a blank one
    /// narrows nothing. <c>pageSize</c> is a request rather than an instruction - the server bounds it
    /// (see <see cref="MasterProductCataloguePaging"/>, default 50 and at most 200) and the response
    /// says which page at which size it actually served.</para>
    /// </summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<SuperAdminMasterProductPageResponse>> List(
        [FromQuery] SuperAdminMasterProductsRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var page = await _list.Handle(
            new MasterProductCatalogueQuery(request.Search, request.Page, request.PageSize),
            cancellationToken);

        return Ok(SuperAdminMasterProductPageResponse.From(page));
    }

    /// <summary>
    /// One master product with every supplier listing that hangs from it: the supplier's own name and
    /// product id, its wording, the pack it describes, the match status, the page it is on, when it
    /// was last checked and what it currently costs.
    /// </summary>
    /// <response code="200">The product and its supplier listings.</response>
    /// <response code="404">No master product has that id.</response>
    [HttpGet("{id:long}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SuperAdminMasterProductDetailResponse>> Get(
        long id,
        CancellationToken cancellationToken)
    {
        var detail = await _get.Handle(id, cancellationToken);

        return detail is null
            ? NotFound()
            : Ok(SuperAdminMasterProductDetailResponse.From(detail));
    }

    /// <summary>
    /// Adds one master product to the shared catalogue: the brand, the name or variant, and the
    /// individual size with its unit from the closed list.
    ///
    /// <para>Normalised and keyed exactly as the Supplier Price Review's own create is, so a product
    /// added here and the same product added while deciding a match are one row and not two. Nothing
    /// else is written: no supplier listing is created, confirmed or refused, and no business's
    /// tracked product is matched or unmatched.</para>
    /// </summary>
    /// <response code="201">The master product that was created, with its listing counts at zero.</response>
    /// <response code="400">
    /// The brand, name, size or unit is not one this catalogue stores - a blank brand, a size that is
    /// not positive or is finer than three decimal places, a unit that is not one of
    /// <c>ml</c>, <c>l</c>, <c>g</c>, <c>kg</c>, <c>each</c>. Nothing was written.
    /// </response>
    /// <response code="409">
    /// Another master product already describes this brand, name and size. Nothing was written.
    /// </response>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<SuperAdminMasterProductResponse>> Create(
        SuperAdminCreateMasterProductRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var created = await _create.Handle(request.ToSubmission(), cancellationToken);
        var response = SuperAdminMasterProductResponse.From(created);

        return CreatedAtAction(nameof(Get), new { id = created.Id }, response);
    }

    /// <summary>
    /// Corrects one master product: its brand, its name and its size, on the row every business
    /// tracking that product already points at.
    ///
    /// <para>The row keeps its id, so every tracking mapping, supplier listing, match suggestion and
    /// collector work item that names it still names it afterwards, and no other row is changed. The
    /// normalised key and <c>updatedAtUtc</c> are recomputed from what was submitted.</para>
    ///
    /// <para>The request carries the <c>lastChangedAtUtc</c> the page read, and an edit that presents
    /// one the row no longer holds is refused rather than applied: two Super Admin tabs cannot
    /// overwrite each other silently.</para>
    /// </summary>
    /// <response code="200">The master product as it now stands.</response>
    /// <response code="400">
    /// The submitted description is not one this catalogue stores, or the request carries no
    /// <c>lastChangedAtUtc</c>. Nothing was written.
    /// </response>
    /// <response code="404">No master product has that id.</response>
    /// <response code="409">
    /// Another master product already describes this brand, name and size; or the product has been
    /// changed since this page read it; or the <strong>size</strong> would change while a supplier
    /// listing of the product is confirmed, whose pack size was confirmed against the current size.
    /// Nothing was written.
    /// </response>
    [HttpPut("{id:long}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<SuperAdminMasterProductResponse>> Update(
        long id,
        SuperAdminUpdateMasterProductRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var updated = await _update.Handle(id, request.ToSubmission(), cancellationToken);

        return updated is null
            ? NotFound()
            : Ok(SuperAdminMasterProductResponse.From(updated));
    }
}
