using Inventory.Application.Access;
using Inventory.Application.PriceCatalogue;
using InventoryApi.Auth;
using InventoryApi.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

/// <summary>
/// The shared cheapest-supplier summary (issue #930, parent #817): which of the business's own
/// suppliers is cheapest per vending unit for each product on a page, with every supplier's offer
/// beside it.
///
/// <para><strong>One endpoint for three pages.</strong> The Dashboard's Reorder Alerts (#939), Needs
/// Ordering and Products (#940) all read this one summary with their own rows' product ids, which is
/// what stops the three columns drifting - and it is the reason this is a controller of its own
/// rather than another action on <see cref="ProductsController"/>: a controller declares one
/// capability, and the three pages that show this column are reached by every role that may open the
/// Dashboard.</para>
///
/// <para><strong>It requires <c>Dashboard.View</c></strong>, which Operator, Manager and Owner all
/// hold (<c>RoleCapabilities</c>): an observed shelf price is purchasing guidance about a pack
/// nobody has bought, not a recorded cost, so it is not behind the financial capability that guards
/// what the business actually paid (<c>Dashboard.ViewFinancials</c>, issue #788's purchase-cost
/// summary).</para>
///
/// <para><strong>It is a POST that changes nothing</strong>, and only because of the id list: a page
/// of up to 200 product ids does not belong in a query string. That is the review finding of
/// 27 October 2026, which fixed both the method and the maximum. One consequence is deliberate and
/// worth stating: a read-only support session refuses every non-<c>GET</c> request by its own method
/// rule (issue #551), so this column reads as unavailable in a support view rather than being
/// specially permitted here.</para>
///
/// <para>The controller is thin: it binds the ids, invokes the use case and maps its answer. Every
/// comparison, GST conversion and tie is <c>Inventory.Domain.PriceCatalogue.CheapestUnitOfferPolicy</c>'s
/// (issue #925); nothing here divides, converts, rounds or chooses a cheapest.</para>
/// </summary>
[ApiController]
[Route("api/cheapest-suppliers")]
[Authorize]
[RequiredScope("access_as_user")]
[RequireCapability(Capability.DashboardView)]
public sealed class CheapestSuppliersController : ControllerBase
{
    private readonly GetCheapestSupplierSummary _getSummary;

    public CheapestSuppliersController(GetCheapestSupplierSummary getSummary)
    {
        _getSummary = getSummary;
    }

    /// <summary>
    /// The cheapest-supplier answer for the products a page is showing.
    ///
    /// <para>The business is resolved from the caller's own membership and every row is scoped by
    /// the ordinary tenant query filters, so an id belonging to another business matches nothing and
    /// is absent from the answer - never an error that would confirm the id exists somewhere.</para>
    /// </summary>
    /// <response code="200">The summary, one entry per asked-for product of this business.</response>
    /// <response code="400">
    /// No product ids at all, or more than the accepted maximum. Nothing is read.
    /// </response>
    /// <response code="403">
    /// The caller's role does not grant <c>Dashboard.View</c>, or the request is in a read-only
    /// support session, which refuses every non-<c>GET</c> request.
    /// </response>
    [HttpPost("summary")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<CheapestSupplierSummaryResponse>> Summary(
        CheapestSupplierSummaryRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var summary = await _getSummary.Handle(request.ProductIds, ct);

        return Ok(new CheapestSupplierSummaryResponse(
            summary.DisplayBasis,
            [.. summary.Products.Select(ToResponse)]));
    }

    private static CheapestSupplierProductResponse ToResponse(CheapestSupplierProductSummary product) =>
        new(
            product.ProductId,
            product.TrackingStatus,
            product.CheapestUnitPrice,
            [.. product.Suppliers.Select(ToResponse)]);

    private static CheapestSupplierOfferResponse ToResponse(SupplierUnitPriceOffer offer) =>
        new(
            offer.SupplierId,
            offer.SupplierName,
            offer.HasListing,
            offer.HasPrice,
            offer.PackQuantity,
            offer.PackPrice,
            offer.PackPriceBasis,
            offer.UnitPrice,
            offer.UnitPriceExGst,
            offer.UnitPriceIncGst,
            offer.IsSpecial,
            offer.PromotionEndsAtUtc,
            offer.StockStatus,
            offer.LastCheckedAtUtc,
            offer.ProductUrl,
            offer.Ineligibility,
            offer.IsCheapest);
}
