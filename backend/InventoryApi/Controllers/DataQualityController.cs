using Inventory.Application.Access;
using Inventory.Application.CatalogReconciliation;
using InventoryApi.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

/// <summary>
/// The read-only Nayax catalogue reconciliation (issue #55): how this business's live Nayax
/// products and machines have drifted from its local records.
///
/// It requires <c>Integration.Manage</c> (issue #526), so an Owner alone. Issue #526 says this
/// controller follows the page that calls it, and no page does: nothing in the Angular application
/// requests this route. The capability therefore follows what the endpoint <em>is</em> - a live
/// read of the business's own Nayax account, reported so somebody can decide whether the mapping
/// needs fixing - and the Owner-only capability for that integration is the narrowest honest
/// answer. <c>Data.Repair</c> would grant exactly the same roles today; this one was chosen because
/// the endpoint reports on the integration and repairs nothing. See docs/architecture.md
/// § Capability authorization on the Manager and Owner endpoints.
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
