using Inventory.Application.Products;
using Inventory.Application.Purchases;
using Inventory.Application.Reporting.Dashboard;
using InventoryApi.Adapters.Mapping;
using InventoryApi.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
[RequiredScope("access_as_user")]
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

    public ProductsController(
        ListProducts listProducts,
        GetProduct getProduct,
        ListLowStockProducts listLowStockProducts,
        CreateProduct createProduct,
        UpdateProduct updateProduct,
        DeleteProduct deleteProduct,
        GetInventoryValuationSummary getInventoryValuationSummary,
        GetProductPriceComparison getProductPriceComparison)
    {
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
        var products = await _listProducts.Handle(
            new ProductCatalogFilter(search, categoryId, supplierId), lowStockOnly, ct);
        return Ok(products.Select(ProductRecordResponseMapper.ToResponse).ToList());
    }

    [HttpGet("{id:long}")]
    public async Task<ActionResult<ProductResponse>> Get(long id)
    {
        var product = await _getProduct.Handle(id, CancellationToken.None);
        return product is null ? NotFound() : Ok(ProductRecordResponseMapper.ToResponse(product));
    }

    [HttpGet("inventory-value-summary")]
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

    [HttpGet("alerts/low-stock")]
    public async Task<ActionResult<IEnumerable<ProductResponse>>> LowStock(
        [FromQuery] string? search,
        [FromQuery] long? categoryId,
        [FromQuery] int? supplierId,
        CancellationToken ct)
    {
        var items = await _listLowStockProducts.Handle(
            new ProductCatalogFilter(search, categoryId, supplierId), ct);
        return Ok(items.Select(ProductRecordResponseMapper.ToResponse).ToList());
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
}
