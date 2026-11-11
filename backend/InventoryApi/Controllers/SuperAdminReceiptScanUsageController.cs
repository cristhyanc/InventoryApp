using Inventory.Application.PlatformReceiptScans;
using InventoryApi.Auth.PlatformAdmin;
using InventoryApi.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

/// <summary>
/// The Super Admin's view of how many receipt scans one business has used this month (issue #1164,
/// sub-issue C of #975, that issue's own acceptance criterion).
///
/// <para><strong>It is read-only, and that is structural.</strong> There is one route, it is a
/// <c>GET</c>, and the one Application port behind it has one method that returns a count. A platform
/// administrator cannot grant a business extra scans, reset its counter or change its month: buying
/// extra scans is explicitly out of scope on issue #1164, and a counter somebody could edit is not
/// evidence of what was spent.</para>
///
/// <para><strong>Authorisation is the other Super Admin surfaces', unchanged.</strong>
/// <c>[Authorize(Policy = PlatformAdminPolicy.Name)]</c> is evaluated by the authorization middleware
/// before any action here runs, and <c>BusinessScopeMiddleware</c> re-evaluates the same policy
/// before it allows the request past the membership requirement - class 2 of the five ways an endpoint
/// may declare its authorization (<c>EndpointAuthorizationClassificationTests</c>), with both halves
/// required, and no new class or marker. There is no token claim, header, route value or body field
/// that selects the authority: it is the configured Entra <c>(tid, oid)</c> pair and nothing else, and
/// both values empty - the shipped state - means nobody can reach this controller at all. An Owner of
/// a business is refused here exactly as a Manager or an Operator is, because a business role is not
/// platform authority.</para>
///
/// <para><strong>What it may reach is one counter.</strong> The request carries a <em>denied</em>
/// <c>BusinessScope</c> throughout. What this surface reads is the business row it names and that
/// business's own <c>ReceiptScanUsages</c> row for the month that business is in - through
/// <see cref="IPlatformReceiptScanUsageStore"/>, which is its own narrow port and deliberately not a
/// widening of the business directory store. No receipt, draft, purchase, product, supplier, sale,
/// stock, cost, machine, site, document, credential or plan value is readable here, and nothing is
/// writable at all.</para>
///
/// <para><strong>The business id in the route is the subject of the read, not a tenancy
/// scope.</strong> It is checked against the stored business row - so an unknown id is <c>404</c> -
/// and the answer is addressed to the key that row carries.</para>
///
/// <para>The controller is thin, as every controller here is: it binds the route value, invokes the
/// use case with the request's cancellation token, and maps the result to HTTP.</para>
/// </summary>
[ApiController]
[Route("api/super-admin/businesses/{businessId:int}/receipt-scan-usage")]
// Both, and both are enforced: the default policy carries the delegated-scope requirement that
// [RequiredScope] declares, and the named policy carries the platform-admin one. A named policy
// alone would replace the default rather than add to it, which would leave the scope undemanded.
[Authorize]
[Authorize(Policy = PlatformAdminPolicy.Name)]
[RequiredScope("access_as_user")]
[PlatformDiagnosticsEndpoint]
public sealed class SuperAdminReceiptScanUsageController : ControllerBase
{
    private readonly GetBusinessReceiptScanUsage _usage;

    public SuperAdminReceiptScanUsageController(GetBusinessReceiptScanUsage usage)
    {
        _usage = usage;
    }

    /// <summary>
    /// How many receipt scans that business has used in its own current calendar month, how many are
    /// left and the cap.
    /// </summary>
    /// <response code="200">The business's month, its count, what is left and the cap.</response>
    /// <response code="404">
    /// No such business. A business that has never scanned is <strong>not</strong> a 404: it has
    /// used none of its allowance, which is a count of zero.
    /// </response>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SuperAdminReceiptScanUsageResponse>> Get(
        int businessId,
        CancellationToken cancellationToken)
    {
        var view = await _usage.Handle(businessId, cancellationToken);

        return view is null ? NotFound() : Ok(SuperAdminReceiptScanUsageResponse.From(view));
    }
}
