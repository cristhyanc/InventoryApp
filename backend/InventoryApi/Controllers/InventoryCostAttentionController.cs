using Inventory.Application.Access;
using Inventory.Application.Costing;
using InventoryApi.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

// The Dashboard's "costs need attention" read: the products whose inventory-cost replay failed and
// is still pending (issue #1103's durable rows). A product whose cost history cannot be replayed no
// longer fails POST api/nayax-sales-sync - the sales are stored either way - so this is where that
// failure is shown, by product, instead of a business-wide "could not be synchronized" warning.
//
// It follows the page that calls it, the home Dashboard, so it requires Dashboard.View like the
// sync. It is a read of this business's own rows only (the central tenant query filter), carries no
// price, cost, value or quantity, and makes no provider call, so no plan gates it.
[ApiController]
[Route("api/inventory-cost-attention")]
[Authorize]
[RequiredScope("access_as_user")]
[RequireCapability(Capability.DashboardView)]
public sealed class InventoryCostAttentionController : ControllerBase
{
    private readonly GetProductsNeedingCostAttention _getProducts;

    public InventoryCostAttentionController(GetProductsNeedingCostAttention getProducts)
    {
        _getProducts = getProducts;
    }

    /// <summary>
    /// Lists this business's products whose last inventory-cost replay failed and is still pending,
    /// oldest first. An empty list means nothing is waiting on a failed replay.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ProductNeedingCostAttention>>> List(CancellationToken cancellationToken) =>
        Ok(await _getProducts.Handle(cancellationToken));
}
