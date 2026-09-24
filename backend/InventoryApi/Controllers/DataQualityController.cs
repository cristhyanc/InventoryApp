using Inventory.Application.CatalogReconciliation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

[ApiController]
[Route("api/data-quality")]
[Authorize]
[RequiredScope("access_as_user")]
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
