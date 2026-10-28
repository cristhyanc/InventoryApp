using Inventory.Application.PriceCatalogue;
using InventoryApi.Auth.PriceCollector;
using InventoryApi.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InventoryApi.Controllers;

/// <summary>
/// The scheduled price collector's own surface (issue #924, parent #817): the work list a collector
/// run reads, and nothing else.
///
/// <para><strong>The caller is an application, not a person.</strong> It authenticates with a
/// short-lived Microsoft Entra app-only token obtained through GitHub OIDC workload identity
/// federation, so no Restockroo password and no client secret exists anywhere. The token must satisfy
/// all of: a valid signature, issuer and lifetime; this API's own audience; the
/// <c>PriceCollector.Run</c> application role; <strong>no</strong> delegated scope at all; the
/// configured collector tenant in <c>tid</c>; and the configured collector application in
/// <c>azp</c>/<c>appid</c>. The first four are the bearer scheme's rules and all six are the policy's,
/// so the answer does not depend on which scheme authenticated the request - see
/// <see cref="PriceCollectorBearerScheme"/> and <see cref="PriceCollectorPolicy"/>. With the tenant
/// and client id unset - the shipped state - every request here is <c>401</c>.</para>
///
/// <para><strong>It is class 5 of the endpoint authorization classes, and the only one that is not a
/// person's</strong> (<c>EndpointAuthorizationClassificationTests</c>). It carries no
/// <c>[RequireCapability]</c>, because a capability is a business role's power and the collector holds
/// no role in any business; and no <c>[RequiredScope]</c>, because a delegated scope is a person's
/// consent and there is no person. It names its own authentication scheme, which is what keeps the
/// app-only path to this route and off every other endpoint: an app-only token presented anywhere
/// else is refused by that endpoint's own scheme, exactly as it was before this issue.</para>
///
/// <para><strong>No business is involved, in either direction.</strong> The request carries a
/// <em>denied</em> <c>BusinessScope</c>, so the tenant query filters and
/// <c>BusinessOwnershipEnforcer</c> stay fully in force. The only tenant-owned data this surface may
/// reach is the business-free search text of <c>IPriceCatalogueWorkListStore</c>'s reviewed
/// cross-business read - a waiting key, a search name, a unit label and a master product id, none of
/// which says whose product it is. The response carries no business id, business name, business
/// product id or price at all (<see cref="PriceCollectorWorkResponse"/>), and the two tables this
/// endpoint writes are platform tables about no business.</para>
///
/// <para><strong>Reading the work list is a write, and that is deliberate.</strong> Issuing a run is
/// what makes a result attributable: the run id and one work item per product-and-supplier pair are
/// stored before the list is answered, and issuing a new run expires whatever run was still open, in
/// the same transaction. It is therefore a <c>GET</c> that is not idempotent in the "changes nothing"
/// sense - which is why a second call does not hand out a second open run beside the first.</para>
///
/// <para>The controller is thin, as every controller here is: it invokes one use case with the
/// request's cancellation token and maps the result to HTTP. Which suppliers are due, how the products
/// are deduplicated and how much work one run may carry are the use case's and the Domain's.</para>
/// </summary>
[ApiController]
[Route("api/price-collector")]
// Both, and both are enforced. The named scheme is what makes this the only route in the API an
// app-only token can authenticate on; the named policy is what decides that this particular
// application may use it. Deliberately no [RequiredScope]: a collector token carries no delegated
// scope at all, and requiring one here would make the surface unreachable by the only caller it
// exists for.
[Authorize(
    AuthenticationSchemes = PriceCollectorBearerScheme.Name,
    Policy = PriceCollectorPolicy.Name)]
[PriceCollectorEndpoint]
public sealed class PriceCollectorController : ControllerBase
{
    private readonly IssuePriceCollectorWorkList _issueWorkList;

    public PriceCollectorController(IssuePriceCollectorWorkList issueWorkList)
    {
        _issueWorkList = issueWorkList;
    }

    /// <summary>
    /// Issues one run and answers the work it is to do: the suppliers due on this run, every distinct
    /// tracked product to price, the work item id for each product-and-supplier pair, and - only while
    /// something is waiting for a match - the master products to match against.
    /// </summary>
    /// <response code="200">The work list. It carries no business data of any kind.</response>
    /// <response code="401">
    /// The token is not the configured collector's app-only token, or no collector is configured for
    /// this deployment. Nothing was issued.
    /// </response>
    [HttpGet("work")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<PriceCollectorWorkResponse>> Work(CancellationToken cancellationToken)
    {
        var workList = await _issueWorkList.Handle(cancellationToken);

        return Ok(PriceCollectorWorkResponse.From(workList));
    }
}
