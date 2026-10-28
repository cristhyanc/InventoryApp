using Inventory.Application.Access;
using Inventory.Application.Gst;
using Inventory.Application.PriceCatalogue;
using Inventory.Application.Products;
using Inventory.Application.Purchases;
using Inventory.Application.Reporting.Dashboard;
using Inventory.Domain.PriceCatalogue;
using InventoryApi.Adapters.Mapping;
using InventoryApi.Auth;
using InventoryApi.DTOs;
using InventoryApi.Http;
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
/// <para><b>Since issue #736 the catalogue is also maintained here</b> (parent #329 decision P2):
/// <c>POST</c> creates a product, <c>PUT</c> accepts its name, selling price and category as well as
/// the local fields it always accepted, and <c>DELETE</c> refuses a product anything refers to. They
/// require the same <c>Inventory.Operate</c> as every other action on this controller - maintaining
/// the catalogue is the same day-to-day inventory work as refilling from it - so no new capability
/// is introduced, and a read-only support session is refused by the central rule that allows it only
/// <c>GET</c> and <c>HEAD</c> (issue #551).</para>
///
/// <para><b>The catalogue read actions withhold <c>inventoryValue</c> from a caller without
/// <c>Dashboard.ViewFinancials</c></b> (issue #698). Summing that field over the catalogue gives the
/// valuation issue #527 withholds from the Dashboard and <see cref="InventoryValueSummary"/> refuses
/// outright, so leaving it on these responses left the figure readable by anyone who could list
/// products. The endpoints stay open to every role that may operate inventory - unit cost, selling
/// price and every stock figure are unchanged - and only the response shape differs, decided once
/// per request from <see cref="CurrentCapabilities"/> and never from a role name.</para>
///
/// <para><b>Issue #938 added the "Track supplier prices" switch on the same capability</b> (fourth
/// task of #817): <c>GET price-tracking</c>, <c>GET {id}/price-tracking</c> and
/// <c>PUT {id}/price-tracking</c>. Deciding which of its products a business wants priced at
/// suppliers is the same day-to-day catalogue work as the rest of this controller, which is the
/// "existing product-edit capability" that issue names, so no new capability is declared - and the
/// write is refused in a read-only support session and in a role preview by those features' own
/// method rules rather than by anything written here. The matched status publishes the shared master
/// product's name and size, which is a member-facing read of a platform table bounded by that issue's
/// own review; see docs/architecture.md § Tenant ownership.</para>
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
    /// <c>ProductService</c> delegator wrapped. It was unreachable until issue #736 gave it
    /// <see cref="Create"/>: products used to arrive only through the Nayax catalogue import, which
    /// left a business with no sales provider unable to stock anything at all (parent #329).
    /// </summary>
    private readonly CreateProduct _createProduct;
    private readonly UpdateProduct _updateProduct;
    private readonly DeleteProduct _deleteProduct;
    private readonly GetInventoryValuationSummary _getInventoryValuationSummary;
    private readonly GetProductPriceComparison _getProductPriceComparison;
    private readonly GetProductGstRule _getProductGstRule;
    private readonly SetProductGstRule _setProductGstRule;
    private readonly GetProductPriceTracking _getProductPriceTracking;
    private readonly SetProductPriceTracking _setProductPriceTracking;
    private readonly ListTrackedProducts _listTrackedProducts;
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
        GetProductPriceTracking getProductPriceTracking,
        SetProductPriceTracking setProductPriceTracking,
        ListTrackedProducts listTrackedProducts,
        CurrentCapabilities capabilities)
    {
        _capabilities = capabilities;
        _getProductGstRule = getProductGstRule;
        _setProductGstRule = setProductGstRule;
        _getProductPriceTracking = getProductPriceTracking;
        _setProductPriceTracking = setProductPriceTracking;
        _listTrackedProducts = listTrackedProducts;
        _listProducts = listProducts;
        _getProduct = getProduct;
        _listLowStockProducts = listLowStockProducts;
        _createProduct = createProduct;
        _updateProduct = updateProduct;
        _deleteProduct = deleteProduct;
        _getInventoryValuationSummary = getInventoryValuationSummary;
        _getProductPriceComparison = getProductPriceComparison;
    }

    /// <summary>
    /// The catalogue listing.
    ///
    /// <para><strong>A retired product is not in it</strong> (issue #736): a product with history is
    /// retired through <c>isActive</c> rather than deleted, and a retired product is one the business
    /// has taken out of use - it must not be offered to purchase from, count, pick or refill. The
    /// listing therefore answers the working catalogue, and <c>includeRetired=true</c> is the
    /// explicit way to ask for every product the business owns, which is how a retired one is found
    /// again to reactivate and how a tool that reaches into history lists its products. A single
    /// product is answered by <see cref="Get"/> whether it is retired or not.</para>
    ///
    /// <para>This is a deliberate read-contract change, required by the issue's criterion that a
    /// deactivated product is hidden from lists, pick lists and reorder while keeping all of its
    /// history and staying reactivatable. The Angular screens are issue #737 (B2) and still call
    /// this endpoint without the parameter, so until they pass it the ones that need a retired
    /// product - the catalogue page's reactivation path, the Admin costing-repair and
    /// AVCO-transition tools and the stock-history product filter - see only active products; see
    /// docs/architecture.md § Manual catalogue: who owns a product's fields.</para>
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<ProductResponse>>> GetAll(
        [FromQuery] string? search,
        [FromQuery] long? categoryId,
        [FromQuery] bool? lowStockOnly,
        [FromQuery] bool? includeRetired,
        CancellationToken ct)
    {
        var visibility = await _capabilities.ForCurrentRequestAsync(ct);
        var listing = await _listProducts.Handle(
            new ProductCatalogFilter(search, categoryId, includeRetired == true),
            lowStockOnly,
            ct);
        ReorderListingMetadataHeaders.Publish(Response, listing);
        return Ok(listing.Products
            .Select(product => ProductRecordResponseMapper.ToResponse(product, visibility))
            .ToList());
    }

    /// <summary>
    /// One product, retired or not (issue #736). It is the read that keeps a retired product's facts
    /// and its history reachable while <see cref="GetAll"/> leaves it out of the working catalogue,
    /// and <c>PUT</c> on the same id with <c>isActive: true</c> is how it is reactivated.
    /// </summary>
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
        CancellationToken ct)
    {
        var visibility = await _capabilities.ForCurrentRequestAsync(ct);
        var listing = await _listLowStockProducts.Handle(
            // A retired product never alerts: ProductReorderPolicy.IsReorderAlert requires an
            // active product, and that stays the authority. Excluding them from the read as well
            // only narrows the SQL - it cannot change which rows alert.
            new ProductCatalogFilter(search, categoryId, IncludeRetired: false), ct);
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
    ///
    /// <para>Since issue #895 one more refusal reaches that same <c>400</c>: a request that would
    /// blank a unit label the product already has. A product whose stored label is blank still
    /// accepts a blank one, so the Products list's Deactivate and Reactivate - which send the whole
    /// product back through this action - keep working for a legacy row.</para>
    /// </summary>
    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, ProductUpdateDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        var fields = new ProductUpdateFields(
            dto.Name,
            dto.Sku,
            dto.Description,
            dto.LowStockThreshold,
            dto.RestockTo,
            dto.Unit,
            dto.CategoryId,
            dto.IsActive);

        var result = await _updateProduct.Handle(id, fields, CancellationToken.None);
        return result.Outcome switch
        {
            UpdateProductOutcome.Success => NoContent(),
            UpdateProductOutcome.NotFound => NotFound(),
            _ => BadRequest(result.ValidationError),
        };
    }

    /// <summary>
    /// Creates a product this business maintains itself (issue #736, parent #329 decision P2). The
    /// business comes from the caller's membership and the response is the created product, read back
    /// through the ordinary catalogue read so a client gets the same shape
    /// <see cref="Get"/> answers - including which fields a provider manages, which for a product
    /// created here is none.
    /// </summary>
    /// <response code="201">The created product, with its database-assigned id.</response>
    /// <response code="400">
    /// A validation refusal with its message: no name, no unit label (issue #895), negative restock
    /// settings, or a category this business does not own.
    /// </response>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ProductResponse>> Create(ProductCreateDto dto, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dto);

        var result = await _createProduct.Handle(
            new ProductCreateFields(
                dto.Name,
                dto.Sku,
                dto.Description,
                dto.QuantityInStock,
                dto.LowStockThreshold,
                dto.RestockTo,
                dto.Unit,
                dto.CategoryId,
                dto.IsActive,
                dto.InitialUnitCost),
            ct);

        if (!result.IsValid) return BadRequest(result.ValidationError);

        var visibility = await _capabilities.ForCurrentRequestAsync(ct);
        var created = await _getProduct.Handle(result.ProductId!.Value, ct);

        // The product was created, so this is only reachable if another request deleted it in
        // between - there is no product left to describe, which is what 404 says.
        return created is null
            ? NotFound()
            : CreatedAtAction(
                nameof(Get),
                new { id = created.Id },
                ProductRecordResponseMapper.ToResponse(created, visibility));
    }

    /// <summary>
    /// Deletes a product, when it may be deleted at all.
    ///
    /// <para><strong>A product with history is retired, not deleted</strong> (issue #736, parent #329
    /// decision P2). Its stock movements and machine stock counts would be erased with it, and its
    /// purchase lines, supplier order lines, sales and costing records would make the database refuse
    /// the delete with a message written for a developer, so
    /// <c>Inventory.Domain.Products.ProductDeletionPolicy</c> refuses it first and says what is in
    /// the way. A product the business's sales provider names is refused for a different reason: the
    /// next catalogue import would create it again. In both cases <c>IsActive</c> is how a product is
    /// taken out of use, and nothing is written.</para>
    /// </summary>
    /// <response code="204">The product was deleted.</response>
    /// <response code="404">No such product in this business.</response>
    /// <response code="409">
    /// The refusal message: what refers to the product, or which provider manages it.
    /// </response>
    [HttpDelete("{id:long}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(long id, CancellationToken ct)
    {
        var attempt = await _deleteProduct.Handle(id, ct);
        return attempt.Outcome switch
        {
            ProductWriteOutcome.Succeeded => NoContent(),
            ProductWriteOutcome.Refused => Conflict(attempt.Refusal),
            _ => NotFound(),
        };
    }

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

    /// <summary>
    /// Which of the business's products have supplier prices tracked, and where each one has got to
    /// (issue #938, parent #817) - the catalogue listing's "Tracked" column.
    ///
    /// <para>It takes no parameter at all: no filter, no page, no sort, and no business, which is the
    /// caller's own resolved from their membership. Each entry carries a product id and a status and
    /// nothing else, and the list holds only this business's mappings, because the central tenant
    /// query filters match none of any other's.</para>
    ///
    /// <para>Its own resource rather than two fields on the catalogue payload, for the reason the
    /// product's GST rule is one: that payload - and every nested product snapshot a purchase or
    /// supplier-order response carries - is a pinned API contract.</para>
    /// </summary>
    [HttpGet("price-tracking")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<TrackedProductResponse>>> PriceTracking(CancellationToken ct)
    {
        var tracked = await _listTrackedProducts.Handle(ct);

        return Ok(tracked
            .Select(product => new TrackedProductResponse(product.ProductId, product.Status))
            .ToList());
    }

    /// <summary>
    /// Where one product has got to in the shared supplier price catalogue (issue #938): whether this
    /// business tracks it, the mapping's status, the search text it holds, and - only while it is
    /// matched - the master product's name and size.
    ///
    /// <para><strong>The matched description is a read of a platform table from a business endpoint,
    /// and it is bounded by the review of this issue</strong>: the master product's name and size, and
    /// nothing else. It says nothing about any other business, because a master product is a fact
    /// about a brand's product and carries no business identity at all - which is what makes "the
    /// business never sees which other businesses track the same product" true by construction rather
    /// than by filtering. See docs/architecture.md § Tenant ownership.</para>
    ///
    /// <para>It also publishes the eligibility answer the switch needs - <c>canTrack</c> and
    /// <c>trackRefusal</c>, decided by the one
    /// <c>Inventory.Domain.PriceCatalogue.ProductPriceTrackingPolicy</c> that refuses the write - so
    /// the form never words the unit-label rule a second time. The same shape as the product's own
    /// <c>canDelete</c>/<c>deletionRefusal</c>. Those two fields are also what explains a mapping
    /// suspended into <c>awaitingProductDetails</c> because the product's own name or unit label can
    /// no longer be searched for: that mapping names no master product, is sent to no collector, and
    /// resumes when the product is corrected.</para>
    /// </summary>
    /// <response code="200">The product's tracking state, tracked or not.</response>
    /// <response code="404">No such product in this business.</response>
    [HttpGet("{id:long}/price-tracking")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProductPriceTrackingResponse>> GetPriceTracking(
        long id, CancellationToken ct)
    {
        var state = await _getProductPriceTracking.Handle(id, ct);

        return state is null ? NotFound() : Ok(ToResponse(state));
    }

    /// <summary>
    /// Turns the "Track supplier prices" switch on or off for one product (issue #938).
    ///
    /// <para>On creates this business's own mapping with status <c>requested</c>, copying the
    /// product's <c>Name</c> and unit label as the search text (the owner's decision of 27 October
    /// 2026); off removes that mapping and nothing else - no master product, supplier listing or price
    /// is touched, and no other business's mapping is reachable to touch.</para>
    ///
    /// <para>It needs no capability of its own: the controller's existing <c>Inventory.Operate</c> is
    /// the product-edit capability this issue names, and deciding which of its products a business
    /// wants priced is the same day-to-day catalogue work as the rest of this controller. A read-only
    /// support session and a Super Admin's role preview refuse it by their own method rules
    /// (<c>SupportSessionAccessPolicy</c>, <c>RolePreviewPolicy</c>), not by anything written
    /// here - the same way the supplier "Not used" switch and the "Available to add" add are
    /// refused.</para>
    ///
    /// <para>Both directions are idempotent: a product already tracked, and one already untracked,
    /// answer <c>204</c> as well, because the switch states an intention and a double click, a second
    /// tab or a retried request must not need a different answer.</para>
    /// </summary>
    /// <response code="204">The switch is in the state asked for.</response>
    /// <response code="400">
    /// The product may not be tracked, with the rule's own sentence: its unit label is blank or the
    /// generic default. Nothing is written.
    /// </response>
    /// <response code="404">No such product in this business.</response>
    [HttpPut("{id:long}/price-tracking")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetPriceTracking(
        long id, ProductPriceTrackingDto dto, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dto);

        var result = await _setProductPriceTracking.Handle(id, dto.Tracked, ct);

        return result.Outcome switch
        {
            ProductPriceTrackingWriteOutcome.ProductNotFound => NotFound(),
            ProductPriceTrackingWriteOutcome.Refused => BadRequest(RefusalProblem(result.Refusal!)),
            _ => NoContent(),
        };
    }

    /// <summary>
    /// The tracking state as this controller publishes it, with the size unit written as the code
    /// <c>MasterProductPolicy</c> defines rather than as an enum member name.
    /// </summary>
    private static ProductPriceTrackingResponse ToResponse(ProductPriceTrackingState state) =>
        new(
            state.ProductId,
            state.Tracked,
            state.Status,
            state.SearchName,
            state.UnitLabel,
            state.MasterProduct is { } master
                ? new MatchedMasterProductResponse(
                    master.Name,
                    master.SizeValue,
                    MasterProductPolicy.SizeUnitCode(master.SizeUnit))
                : null,
            state.CanTrack,
            state.TrackRefusal);

    /// <summary>
    /// A refusal of the request itself rather than of a field the caller filled in: a product whose
    /// unit label is not one supplier prices can be searched for by (issue #938). It carries the
    /// sentence in <c>detail</c> and in the <c>message</c> extension, the same shape
    /// <see cref="DomainExceptionHandler"/> produces for a domain validation failure, so the existing
    /// client error handling reads it without a second convention.
    /// </summary>
    private static ProblemDetails RefusalProblem(string message)
    {
        var problem = new ProblemDetails
        {
            Title = DomainExceptionHandler.ValidationTitle,
            Detail = message,
            Status = StatusCodes.Status400BadRequest,
        };
        problem.Extensions["message"] = message;
        return problem;
    }
}
