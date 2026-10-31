using Inventory.Application.PriceCatalogue;
using InventoryApi.Auth.PlatformAdmin;
using InventoryApi.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

/// <summary>
/// The Super Admin's view of the shared supplier price catalogue's master products (issue #1023,
/// parent #817): every product the catalogue holds, searchable and one bounded page at a time, and one
/// product with the supplier listings that hang from it.
///
/// <para><strong>It is read only, and that is the whole of this issue.</strong> Before it, the only
/// Super Admin screen over <c>MasterProducts</c> was the Supplier Price Review of issue #928 - a queue
/// of what is waiting for a decision, which publishes the catalogue only as the options of its "match
/// to an existing master product" picker. This controller is how the catalogue itself is browsed and
/// inspected without waiting for something to arrive in that queue. Creating, editing and deleting a
/// master product are separate issues that follow this one, and the review of #928 remains the
/// <em>only</em> writer of a master product in the application.</para>
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
/// and a member presenting a support-session id is still an ordinary member, because this endpoint
/// class never resolves a session.</para>
///
/// <para><strong>The request carries a denied <c>BusinessScope</c> and nothing tenant-owned is read at
/// all.</strong> Every table behind these two endpoints is a platform table about a product or a
/// supplier's shelf: no business id, business name or business product id appears in either response,
/// no price-tracking mapping is read, and no count of the businesses tracking a product is offered -
/// master-catalogue management only, which is the owner's clarification of 31 October 2026. See
/// <c>Inventory.Infrastructure.Persistence.EfMasterProductCatalogueStore</c> and
/// docs/architecture.md § Super Admin master products.</para>
///
/// <para>The controller is thin, as every controller here is: it binds the query string and the route
/// value, invokes a use case with the request's cancellation token, and maps the answer to HTTP. The
/// page bound is <c>MasterProductCataloguePaging</c>'s and the order and the search are the store's.</para>
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

    public SuperAdminMasterProductsController(ListMasterProducts list, GetMasterProduct get)
    {
        _list = list;
        _get = get;
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
}
