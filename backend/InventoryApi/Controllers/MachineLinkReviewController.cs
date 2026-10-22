using Inventory.Application.Access;
using Inventory.Application.Machines;
using InventoryApi.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

/// <summary>
/// The HTTP surface of provider machine linking (issue #742, parent #329 child G3) over the review
/// and apply use cases. Each action binds its request, invokes one use case with the request's
/// cancellation token, and returns the use case's own result unchanged: no link rule, eligibility
/// rule, EF query, transaction or per-call business filter lives here. Ownership is the central one -
/// <c>AppDbContext</c>'s query filters and its ownership stamp on save - and the business is resolved
/// from the authenticated actor's membership inside the use case, so no route, body or header value
/// can name an owner.
///
/// <para><strong>It is a third machine resource, and deliberately so.</strong>
/// <c>/api/local-machines</c> (issue #740) is the machines this business maintains by hand and never
/// touches a provider; <c>/api/machines</c> is the live provider fleet with this business's sales and
/// costs resolved onto it. This one is the join between them, and it is the only surface that writes
/// <c>LocalMachine.ExternalMachineId</c> at all.</para>
///
/// <para>Both actions are <c>POST</c>, including the one that reads. Reviewing the fleet links new
/// provider machines by itself for a business with nothing of its own unlinked, so it is a write even
/// in the case where it finds nothing to do - and a cached <c>GET</c> response would report a fleet
/// it never read.</para>
///
/// <para>Nothing is caught. A decision the fleet or the stored machines refuse throws
/// <c>DomainValidationException</c>, which <c>DomainExceptionHandler</c> maps centrally to a
/// <c>400</c> <c>ProblemDetails</c> carrying the Application's caller-safe message
/// (docs/architecture.md § Domain and application error mapping). A caller with no resolvable business
/// reaches <c>BusinessScopeMiddleware</c>'s <c>403</c>, a business with no usable Nayax connection and
/// a Nayax outage surface through the existing external-integration mapping, and an unexpected failure
/// is deliberately unmapped.</para>
///
/// <para><c>Inventory.Operate</c> is the capability, the one <c>/api/local-machines</c> and the fleet
/// reads already declare: recording which machines you refill is that work. Which also means a
/// read-only support session is refused both of these by the one central rule that allows it only
/// <c>GET</c> and <c>HEAD</c> (issue #551) - there is no per-action check for it in this file, and
/// the Nayax credentials it would need are refused in that state anyway.</para>
/// </summary>
[ApiController]
[Route("api/machine-link-review")]
[Authorize]
[RequiredScope("access_as_user")]
[RequireCapability(Capability.InventoryOperate)]
public sealed class MachineLinkReviewController : ControllerBase
{
    private readonly ReviewProviderMachines _review;
    private readonly ApplyProviderMachineLinks _apply;

    public MachineLinkReviewController(
        ReviewProviderMachines review, ApplyProviderMachineLinks apply)
    {
        _review = review;
        _apply = apply;
    }

    /// <summary>
    /// Reads the provider's fleet and reports it against this business's own machines: the provider
    /// machines no machine of this business carries the id of, and the ones it does. A business with
    /// no machine of its own lacking a provider id gets the new ones linked straight away, as new
    /// machines carrying the provider's name and id; one that has any waits for the decisions
    /// <see cref="Apply"/> carries out. Nothing is matched by name, and no sale, stock movement or
    /// machine count is touched either way.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<ProviderMachineReview>> Review(CancellationToken cancellationToken) =>
        Ok(await _review.Handle(cancellationToken));

    /// <summary>
    /// Records exactly the submitted decisions, as one transaction: link a provider machine to a
    /// machine of this business, or create a new machine for it. One refused decision refuses the
    /// whole apply, and the provider's own name for a created machine comes from this operation's own
    /// fleet read rather than from the request.
    /// </summary>
    [HttpPost("apply")]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ProviderMachineLinksApplied>> Apply(
        [FromBody] ApplyProviderMachineLinksRequest request,
        CancellationToken cancellationToken) =>
        Ok(await _apply.Handle(request, cancellationToken));
}
