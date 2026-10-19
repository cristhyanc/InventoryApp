using Inventory.Application.Access;
using Inventory.Application.CatalogueReview;
using InventoryApi.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

/// <summary>
/// The HTTP surface of the provider catalogue review (issue #738, parent #329 decision C1) over the
/// start, apply and sale-resolution use cases. Each action binds its request, invokes one use case
/// with the request's cancellation token, and returns the use case's own result unchanged: no link
/// rule, field precedence, EF query, transaction, cost replay or per-call business filter lives here.
/// Ownership is the central one - <c>AppDbContext</c>'s query filters and its ownership stamp on save
/// - and the business is resolved from the authenticated actor's membership inside the use case, so no
/// route, body or header value can name an owner.
///
/// <para>All three actions are <c>POST</c>, including the one that previews. Starting a review writes
/// no product, category, price or sale, but it is not a <c>GET</c>: it reads the provider's live
/// catalogue, it records the review the apply is then authorised by, and a cached <c>GET</c> response
/// would hand a caller a review id for a catalogue it never read.</para>
///
/// <para>Nothing is caught. An unknown review, a decision the stored rows refuse and an unconfirmed
/// sale resolution all throw <c>DomainValidationException</c>, which <c>DomainExceptionHandler</c>
/// maps centrally to a <c>400</c> <c>ProblemDetails</c> carrying the Application's caller-safe
/// message (docs/architecture.md § Domain and application error mapping). A caller with no resolvable
/// business reaches <c>BusinessScopeMiddleware</c>'s <c>403</c>, a Nayax outage surfaces through the
/// existing external-integration mapping, and an unexpected failure is deliberately unmapped.</para>
///
/// <para><strong>Two capabilities, and deliberately not one.</strong> Starting and applying a review
/// require <c>Imports.Run</c>, which is what running the catalogue import requires - a review is that
/// import, decided row by row. Attributing historical sales to the products a review linked requires
/// <c>Data.Repair</c>, an Owner alone: it consumes costed inventory, moves product-level revenue and
/// replays cost history, which is the same authority the costing repair and the sale timestamp repair
/// are behind.</para>
/// </summary>
[ApiController]
[Route("api/catalogue-review")]
[Authorize]
[RequiredScope("access_as_user")]
public sealed class CatalogueReviewController : ControllerBase
{
    private readonly StartCatalogueReview _start;
    private readonly ApplyCatalogueReview _apply;
    private readonly ResolveLinkedProviderSales _resolveSales;

    public CatalogueReviewController(
        StartCatalogueReview start,
        ApplyCatalogueReview apply,
        ResolveLinkedProviderSales resolveSales)
    {
        _start = start;
        _apply = apply;
        _resolveSales = resolveSales;
    }

    /// <summary>
    /// Reads the provider's catalogue and reports it against this business's own rows: the provider
    /// products and groups no local row carries the id of, with the option to link one or create one,
    /// and the linked rows whose provider name, category, price or unit/volume has moved. Writes no
    /// product, category, price or sale, and resolves no sale; the returned <c>reviewId</c> is what
    /// <see cref="Apply"/> requires back.
    /// </summary>
    [HttpPost]
    [RequireCapability(Capability.ImportsRun)]
    public async Task<ActionResult<CatalogueReviewDto>> Start(CancellationToken cancellationToken) =>
        Ok(await _start.Handle(cancellationToken));

    /// <summary>
    /// Carries out exactly the decisions submitted against the named review, as one transaction. One
    /// refused link refuses the whole apply; applying the same review twice changes nothing; and no
    /// stored sale's product or historical cost is touched either way.
    /// </summary>
    [HttpPost("apply")]
    [RequireCapability(Capability.ImportsRun)]
    public async Task<ActionResult<CatalogueReviewApplied>> Apply(
        [FromBody] ApplyCatalogueReviewRequest request,
        CancellationToken cancellationToken) =>
        Ok(await _apply.Handle(request, cancellationToken));

    /// <summary>
    /// Attributes the still-unresolved sales carrying a linked product's provider id to that product
    /// and replays the affected products' costing, as one audited transaction. The counts a review
    /// reports are its preview; this is the explicit, confirmed apply, and it only ever fills a sale's
    /// empty product, so a rerun changes nothing.
    /// </summary>
    [HttpPost("unresolved-sales/apply")]
    [RequireCapability(Capability.DataRepair)]
    public async Task<ActionResult<LinkedProviderSaleResolution>> ResolveUnresolvedSales(
        [FromBody] ResolveLinkedProviderSalesRequest request,
        CancellationToken cancellationToken) =>
        Ok(await _resolveSales.Handle(request?.Confirmed ?? false, cancellationToken));
}

/// <summary>
/// The body of the sale-resolution apply. It carries one explicit confirmation and nothing else: the
/// sales, the products and the replay windows are all derived on the server from the provider ids the
/// catalogue review recorded.
/// </summary>
public sealed record ResolveLinkedProviderSalesRequest(bool Confirmed);
