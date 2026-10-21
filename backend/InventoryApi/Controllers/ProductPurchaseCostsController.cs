using Inventory.Application.Access;
using Inventory.Application.Purchases;
using InventoryApi.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

/// <summary>
/// The bulk product purchase-cost summary (issue #788, B of #786): what every product was last
/// bought for and what it was cheapest at, with the supplier of each, in one request instead of one
/// <c>GET api/products/{id}/price-history</c> per product.
///
/// <para>It is its own controller rather than another action on <see cref="ProductsController"/>
/// because the two carry different authorization. The catalogue is <c>Inventory.Operate</c> - an
/// Operator refills machines from it - while this endpoint reports supplier purchasing facts, so it
/// requires <c>Dashboard.ViewFinancials</c> and an Operator is refused. A controller declares one
/// capability for every action it holds (docs/architecture.md § Capability authorization), and the
/// reads behind it are read-only, so a support session may call it under the central
/// <c>GET</c>/<c>HEAD</c> rule (issue #551).</para>
///
/// <para>The figures come from <see cref="ListProductPurchaseCostSummaries"/>, which applies the one
/// authoritative <c>Inventory.Domain.Purchases.SupplierPriceComparisonPolicy</c>. This controller
/// binds the optional product ids and returns the result; it selects, compares and rounds
/// nothing.</para>
/// </summary>
[ApiController]
[Route("api/products")]
[Authorize]
[RequiredScope("access_as_user")]
[RequireCapability(Capability.DashboardViewFinancials)]
public class ProductPurchaseCostsController : ControllerBase
{
    private readonly ListProductPurchaseCostSummaries _listSummaries;

    public ProductPurchaseCostsController(ListProductPurchaseCostSummaries listSummaries) =>
        _listSummaries = listSummaries;

    /// <summary>
    /// Every product of the current business that has at least one recorded purchase line, with its
    /// last and cheapest cost and the supplier each was paid to.
    ///
    /// <para><paramref name="productIds"/> is an optional repeated query value
    /// (<c>?productIds=1&amp;productIds=2</c>) that narrows the answer to those products. Omitted, the
    /// answer covers every product the business owns, <b>retired products included</b> - a retired
    /// product's purchase history is exactly what a tool reaching into history needs. A product with
    /// no purchase history is absent rather than reported with a zero cost, so the array is normally
    /// shorter than the catalogue.</para>
    ///
    /// <para>The business is resolved from the caller's own membership and the rows are scoped by the
    /// ordinary tenant query filters, so an id belonging to another business simply matches nothing
    /// - it is never an error that confirms the id exists somewhere.</para>
    /// </summary>
    /// <response code="200">The summaries, one per product with recorded purchase history.</response>
    /// <response code="403">
    /// The caller's role does not grant <c>Dashboard.ViewFinancials</c> - today an Operator. The
    /// refusal names no capability and no role, like every other capability refusal.
    /// </response>
    [HttpGet("purchase-cost-summary")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IEnumerable<ProductPurchaseCostSummaryDto>>> PurchaseCostSummary(
        [FromQuery] long[]? productIds, CancellationToken ct) =>
        Ok(await _listSummaries.Handle(productIds ?? [], ct));
}
