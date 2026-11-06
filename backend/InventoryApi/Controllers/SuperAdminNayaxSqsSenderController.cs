using Inventory.Application.PlatformNayaxSqs;
using InventoryApi.Auth.PlatformAdmin;
using InventoryApi.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

/// <summary>
/// The Super Admin's Nayax SQS sender identity on one business (issue #1101, child of #1100): read
/// which AWS IAM user sends that business's sales to the shared Restockroo queue, and record, change,
/// switch or remove it.
///
/// <para><strong>Why only a Super Admin.</strong> The recorded id is what tells the queue reader of
/// issue #1103 which business a message belongs to. A business that could choose its own sender id
/// could claim another business's sales, so this record is on no member-facing surface at all.</para>
///
/// <para><strong>Authorisation is the other Super Admin surfaces', unchanged.</strong>
/// <c>[Authorize(Policy = PlatformAdminPolicy.Name)]</c> is evaluated by the authorization middleware
/// before any action here runs, and <c>BusinessScopeMiddleware</c> re-evaluates the same policy
/// before it allows the request past the membership requirement - class 2 of the five ways an
/// endpoint may declare its authorization (<c>EndpointAuthorizationClassificationTests</c>), with
/// both halves required, and no new class or marker. There is no token claim, header, route value or
/// body field that selects the authority: it is the configured Entra <c>(tid, oid)</c> pair and
/// nothing else, and both values empty - the shipped state - means nobody can reach this controller
/// at all. An Owner of a business is refused here exactly as a Manager or an Operator is, because a
/// business role is not platform authority. A role preview is refused by <c>RolePreviewMiddleware</c>
/// like every other write, and a read-only support session is refused by its own HTTP-method rule and
/// again by <c>BusinessOwnershipEnforcer</c>.</para>
///
/// <para><strong>What it may write is one record, and nothing of the business's own data.</strong>
/// The request carries a <em>denied</em> <c>BusinessScope</c> throughout. What this surface reaches
/// is the business row it names, that business's sender row, and the append-only audit row written in
/// the same transaction - through <see cref="IPlatformNayaxSqsSenderStore"/>, which is its own narrow
/// port and deliberately not a widening of the business directory store. No Nayax Lynx credential, no
/// sale, stock, cost, purchase, product, machine, site, document or report value is readable or
/// writable here, and <strong>nothing is ever deleted</strong>: there is no <c>DELETE</c> route,
/// because "Remove" is the audited update of <c>PUT</c> that clears the id.</para>
///
/// <para><strong>The business id in the route is the subject of the decision, not a tenancy
/// scope.</strong> It is checked against the stored business row inside the write transaction - so an
/// unknown id is <c>404</c> - and the write is addressed to the key that row carries. Each write
/// presents the revision the caller read, and anything else is <c>409</c>, so two Super Admin tabs
/// cannot overwrite each other.</para>
///
/// <para>The controller is thin, as every controller here is: it binds the route value and the body,
/// invokes the use case with the request's cancellation token, and maps the outcome to HTTP. No rule,
/// transaction, revision comparison or EF query lives here, and the refusals that carry a sentence
/// are <c>DomainValidationException</c> and <c>DomainConflictException</c>, which
/// <c>DomainExceptionHandler</c> answers as <c>400</c> and <c>409</c> with the Domain's own
/// caller-safe messages.</para>
/// </summary>
[ApiController]
[Route("api/super-admin/businesses/{businessId:int}/nayax-sqs-sender")]
// Both, and both are enforced: the default policy carries the delegated-scope requirement that
// [RequiredScope] declares, and the named policy carries the platform-admin one. A named policy
// alone would replace the default rather than add to it, which would leave the scope undemanded.
[Authorize]
[Authorize(Policy = PlatformAdminPolicy.Name)]
[RequiredScope("access_as_user")]
[PlatformDiagnosticsEndpoint]
public sealed class SuperAdminNayaxSqsSenderController : ControllerBase
{
    private readonly ManageBusinessNayaxSqsSender _senders;

    public SuperAdminNayaxSqsSenderController(ManageBusinessNayaxSqsSender senders)
    {
        _senders = senders;
    }

    /// <summary>
    /// The business's recorded sender as it stands, with the revision the next write has to present.
    /// </summary>
    /// <response code="200">The recorded sender.</response>
    /// <response code="404">
    /// No such business, or no sender row has ever existed for it - "nothing is recorded" is the
    /// absence of a resource rather than an empty one, and the first write presents
    /// <c>expectedRevision: null</c> rather than a revision read from here.
    /// </response>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SuperAdminNayaxSqsSenderResponse>> Get(
        int businessId,
        CancellationToken cancellationToken)
    {
        var view = await _senders.GetAsync(businessId, cancellationToken);

        return view is null ? NotFound() : Ok(SuperAdminNayaxSqsSenderResponse.From(view));
    }

    /// <summary>
    /// Records the business's sender, or changes, switches or removes the one it holds. A null
    /// <c>awsSenderId</c> with <c>isEnabled: false</c> is Remove, which keeps the row and its audit
    /// history and frees the id for another business.
    /// </summary>
    /// <response code="200">The sender as it was written.</response>
    /// <response code="400">
    /// The sender id is not 16 to 128 letters and digits, or the mapping was switched on with no
    /// sender id. Nothing was written.
    /// </response>
    /// <response code="404">No such business.</response>
    /// <response code="409">
    /// The revision is not the stored one - the row changed since it was read, or the caller asked to
    /// create a first row for a business that already has one - the business is not active, or the
    /// sender id is already recorded for another business. Nothing was written, and no refusal names
    /// another business.
    /// </response>
    [HttpPut]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<SuperAdminNayaxSqsSenderResponse>> Put(
        int businessId,
        SuperAdminNayaxSqsSenderRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var view = await _senders.SetAsync(
            businessId,
            request.AwsSenderId,
            request.IsEnabled,
            request.ExpectedRevision,
            cancellationToken);

        return view is null ? NotFound() : Ok(SuperAdminNayaxSqsSenderResponse.From(view));
    }
}
