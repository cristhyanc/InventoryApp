using Inventory.Application.Access;
using Inventory.Application.CatalogReconciliation;
using InventoryApi.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

/// <summary>
/// The Nayax catalogue source-state reconciliation (issue #55): how the business's live Nayax
/// catalogue has drifted from its local records.
///
/// <para>Requires <c>Integration.Manage</c> (issue #526), which only an Owner holds. Issue #526
/// says this controller follows the page that calls it, and the reviewed answer is that <em>no page
/// calls it</em>: nothing in the Angular application requests this route today. Left unclassified
/// it would stay reachable by every member, so it takes the Owner-only capability that matches what
/// it is - a diagnostic about the state of this business's own Nayax integration, read before
/// deciding whether the catalogue needs repairing. It is listed in the pull request for review
/// precisely because the mapping is a judgement rather than a page's own rule, and it is the
/// fail-closed one: a future Owner-facing integration page can show it with no change here, and
/// anything wider is a decision to take deliberately.</para>
/// </summary>
[ApiController]
[Route("api/data-quality")]
[Authorize]
[RequiredScope("access_as_user")]
[RequireCapability(Capability.IntegrationManage)]
public sealed class DataQualityController : ControllerBase
{
    private readonly GetNayaxCatalogReconciliation _getNayaxCatalogReconciliation;

    public DataQualityController(GetNayaxCatalogReconciliation getNayaxCatalogReconciliation)
    {
        _getNayaxCatalogReconciliation = getNayaxCatalogReconciliation;
    }

    /// <summary>
    /// Compares current Nayax products/machines against local history and returns every identity's
    /// source state (present, added, missing remotely, mapping changed, or conflicting identity) for
    /// human review. Read-only: it never deletes or changes a local record.
    /// </summary>
    [HttpGet("nayax-catalog-reconciliation")]
    public Task<CatalogReconciliationReportDto> NayaxCatalogReconciliation(CancellationToken cancellationToken) =>
        _getNayaxCatalogReconciliation.Handle(cancellationToken);
}
