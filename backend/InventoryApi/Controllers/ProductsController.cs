using Inventory.Application.Access;
using Inventory.Application.Gst;
using Inventory.Application.Products;
using Inventory.Application.Purchases;
using Inventory.Application.Reporting.Dashboard;
using InventoryApi.Adapters.Mapping;
using InventoryApi.Auth;
using InventoryApi.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

/// <summary>
/// The product catalogue, its stock-planning projections and each product's GST rule. Day-to-day
/// inventory work, so every action requires <c>Inventory.Operate</c> (issue #525) - the catalogue is
/// what an Operator refills machines from, and the product GST rule is configuration an Operator
/// maintains alongside it rather than a separate capability in the agreed table.
///
/// <para><b>The catalogue read actions withhold <c>inventoryValue</c> from a caller without
/// <c>Dashboard.ViewFinancials</c></b> (issue #698). Summing that field over the catalogue gives the
/// valuation issue #527 withholds from the Dashboard and <see cref="InventoryValueSummary"/> refuses
/// outright, so leaving it on these responses left the figure readable by anyone who could list
/// products. The endpoints stay open to every role that may operate inventory - unit cost, selling
/// price and every stock figure are unchanged - and only the response shape differs, decided once
/// per request from <see cref="CurrentCapabilities"/> and never from a role name.</para>
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
[RequiredScope("access_as_user")]
[RequireCapability(Capability.InventoryOperate)]
public class ProductsController : ControllerBase
{
    private readonly ListProducts _listProducts;
    private readonly GetProduct _getProduct;
    private readonly ListLowStockProducts _listLowStockProducts;

    /// <summary>
    /// Injected per issue #303's acceptance criteria, which list every product use case the retired
    /// <c>ProductService</c> delegator wrapped. The API has never exposed a product-create route -
    /// products arrive through the Nayax catalogue import - so no action invokes it yet; adding or
    /// dropping an endpoint would be an API contract change this issue excludes.
    /// </summary>
    private readonly CreateProduct _createProduct;
    private readonly UpdateProduct _updateProduct;
    private readonly DeleteProduct _deleteProduct;
    private readonly GetInventoryValuationSummary _getInventoryValuationSummary;
    private readonly GetProductPriceComparison _getProductPriceComparison;
    private readonly GetProductGstRule _getProductGstRule;
    private readonly SetProductGstRule _setProductGstRule;
    private readonly CurrentCapabilities _capabilities;

    public ProductsController(
        ListProducts listProducts,
        GetProduct getProduct,
        ListLowStockProducts listLowStockProducts,
        CreateProduct createProduct,
        UpdateProduct updateProduct,
        DeleteProduct deleteProduct,
        GetInventoryValuationSummary getInventoryValuationSummary,
        GetProductPriceComparison getProductPriceComparison,
        GetProductGstRule getProductGstRule,
        SetProductGstRule setProductGstRule,
        CurrentCapabilities capabilities)
    {
        _capabilities = capabilities;
        _getProductGstRule = getProductGstRule;
        _setProductGstRule = setProductGstRule;
        _listProducts = listProducts;
        _getProduct = getProduct;
        _listLowStockProducts = listLowStockProducts;
        _createProduct = createProduct;
        _updateProduct = updateProduct;
        _deleteProduct = deleteProduct;
        _getInventoryValuationSummary = getInventoryValuationSummary;
        _getProductPriceComparison = getProductPriceComparison;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<ProductResponse>>> GetAll(
        [FromQuery] string? search,
        [FromQuery] long? categoryId,
        [FromQuery] int? supplierId,
        [FromQuery] bool? lowStockOnly,
        CancellationToken ct)
    {
        var visibility = await _capabilities.ForCurrentRequestAsync(ct);
        var listing = await _listProducts.Handle(
            new ProductCatalogFilter(search, categoryId, supplierId), lowStockOnly, ct);
        ReorderListingMetadataHeaders.Publish(Response, listing);
        return Ok(listing.Products
            .Select(product => ProductRecordResponseMapper.ToResponse(product, visibility))
            .ToList());
    }

    [HttpGet("{id:long}")]
    public async Task<ActionResult<ProductResponse>> Get(long id)
    {
        var visibility = await _capabilities.ForCurrentRequestAsync(CancellationToken.None);
        var product = await _getProduct.Handle(id, CancellationToken.None);
        return product is null ? NotFound() : Ok(ProductRecordResponseMapper.ToResponse(product, visibility));
    }

    /// <summary>
    /// The business's inventory valuation: quantity at cost, and the total value of it.
    ///
    /// <para><strong>It needs <c>Dashboard.ViewFinancials</c> as well as the controller's
    /// <c>Inventory.Operate</c></strong> (issue #681). Issue #527 established that the exact value
    /// of a business's inventory at cost is a financial figure an Operator is not shown: the
    /// dashboard omits <c>inventory.inventoryValueAtCost</c> from the response entirely for a role
    /// without that capability. This endpoint reports the same figure, broken down, so leaving it on
    /// the catalogue's own capability made the dashboard's redaction a presentation detail rather
    /// than a boundary - an Operator could read the number directly. Capability requirements
    /// accumulate down an endpoint, so this narrows one action and widens nothing: a Manager and an
    /// Owner hold both capabilities and are unaffected.</para>
    /// </summary>
    /// <response code="200">The valuation summary.</response>
    /// <response code="403">
    /// The caller's role does not grant <c>Dashboard.ViewFinancials</c> - today an Operator. The
    /// refusal names no capability and no role, like every other capability refusal.
    /// </response>
    [HttpGet("inventory-value-summary")]
    [RequireCapability(Capability.DashboardViewFinancials)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<InventoryValuationSummaryDto> InventoryValueSummary(CancellationToken ct) =>
        _getInventoryValuationSummary.Handle(ct);

    [HttpGet("{id:long}/price-history")]
    public async Task<ActionResult<ProductPriceComparisonDto>> PriceHistory(long id, CancellationToken ct)
    {
        if (await _getProduct.Handle(id, ct) is null)
        {
            return NotFound();
        }

        return Ok(await _getProductPriceComparison.Handle(id, ct));
    }

    /// <summary>
    /// The reorder alerts for the filtered catalogue.
    ///
    /// <para>The body is the same JSON array it has always been. Since issue #726 reorder has two
    /// modes, so the response also carries <c>X-Reorder-Mode</c> and - in storage-only mode -
    /// <c>X-Reorder-Machine-Need-Unavailable-Reason</c>, because a row with
    /// <c>machineReplenishmentNeed: 0</c> means something different in each mode and an empty list
    /// still has a mode. See <see cref="ReorderListingMetadataHeaders"/>. The same metadata is
    /// published by <see cref="GetAll"/> when <c>lowStockOnly=true</c>, which is this same use
    /// case.</para>
    /// </summary>
    [HttpGet("alerts/low-stock")]
    public async Task<ActionResult<IEnumerable<ProductResponse>>> LowStock(
        [FromQuery] string? search,
        [FromQuery] long? categoryId,
        [FromQuery] int? supplierId,
        CancellationToken ct)
    {
        var visibility = await _capabilities.ForCurrentRequestAsync(ct);
        var listing = await _listLowStockProducts.Handle(
            new ProductCatalogFilter(search, categoryId, supplierId), ct);
        ReorderListingMetadataHeaders.Publish(Response, listing);
        return Ok(listing.Products
            .Select(item => ProductRecordResponseMapper.ToResponse(item, visibility))
            .ToList());
    }

    /// <summary>
    /// The one deliberate, human-approved error-path change in issue #303 (approved by the repository
    /// owner in the review of PR #355). Every outcome a client can cause is unchanged: 204 on success,
    /// 404 for an unknown id - still answered before validation - and 400 with the same
    /// <see cref="UpdateProductResult.ValidationError"/> text for invalid restock settings.
    ///
    /// What changed is the failure path the retired delegator could not distinguish: it signalled
    /// invalid settings by throwing <see cref="InvalidOperationException"/>, so this action had to
    /// wrap the call in a broad <c>catch (InvalidOperationException)</c> that also converted an
    /// unexpected failure from below the use case - an exhausted connection pool, a programming
    /// error - into a 400 echoing that exception's internal message. The use case now reports
    /// validation as an <see cref="UpdateProductOutcome"/>, so the catch is gone and such a failure
    /// propagates to <see cref="InventoryApi.Http.GlobalExceptionHandler"/>, which logs it once and
    /// answers a generic 500 that carries no exception message - the same shape issue #59 gave
    /// <c>StockController</c>. Pinned by
    /// <c>InventoryApi.Tests.Controllers.ProductsControllerTests</c>'s update error-path tests.
    /// </summary>
    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, ProductUpdateDto dto)
    {
        var fields = new ProductUpdateFields(
            dto.Sku, dto.Description, dto.LowStockThreshold, dto.RestockTo, dto.Unit, dto.SupplierId, dto.IsActive);

        var result = await _updateProduct.Handle(id, fields, CancellationToken.None);
        return result.Outcome switch
        {
            UpdateProductOutcome.Success => NoContent(),
            UpdateProductOutcome.NotFound => NotFound(),
            _ => BadRequest(result.ValidationError),
        };
    }

    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(long id) =>
        await _deleteProduct.Handle(id, CancellationToken.None) ? NoContent() : NotFound();

    /// <summary>
    /// The product's configured GST rule (issue #430). It is its own resource rather than a field on
    /// the catalogue response because the product payload - and every nested product snapshot a
    /// purchase or supplier-order response carries - is a pinned API contract; the reasoning is on
    /// the persisted <c>GstRule</c> property itself and in docs/architecture.md § Product and
    /// supplier GST rules.
    /// </summary>
    [HttpGet("{id:long}/gst-rule")]
    public async Task<ActionResult<ProductGstRuleResponse>> GetGstRule(long id, CancellationToken ct)
    {
        var rule = await _getProductGstRule.Handle(id, ct);
        return rule is null ? NotFound() : Ok(new ProductGstRuleResponse(id, rule.Value));
    }

    /// <summary>
    /// Sets the product's GST rule. Configuration only: it reclassifies no purchase line or charge,
    /// and changes no cost, costing quantity, inventory value or stock movement.
    /// </summary>
    [HttpPut("{id:long}/gst-rule")]
    public async Task<IActionResult> SetGstRule(long id, ProductGstRuleDto dto, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dto);

        var result = await _setProductGstRule.Handle(id, dto.GstRule, ct);
        return result.Outcome switch
        {
            GstRuleUpdateOutcome.Success => NoContent(),
            GstRuleUpdateOutcome.NotFound => NotFound(),
            _ => BadRequest(result.ValidationError),
        };
    }
}
