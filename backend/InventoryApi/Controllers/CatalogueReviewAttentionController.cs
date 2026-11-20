using Inventory.Application.Access;
using Inventory.Application.CatalogueReview;
using InventoryApi.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

/// <summary>
/// The Dashboard's "Nayax products are selling but aren't in your products" read (issue #1352).
///
/// <para><strong>Why it is its own controller beside the catalogue review.</strong> It sits under the
/// same route prefix because it is about the same work - what the catalogue import has left to do -
/// but <see cref="CatalogueReviewController"/> publishes three deliberately reviewed <c>POST</c>
/// surfaces over the catalogue and over historical sale attribution, and a pinned contract test holds
/// it to exactly those three. This is a <c>GET</c> that reads stored rows, writes nothing and decides
/// nothing, so it is declared separately rather than widening that controller - the same shape the
/// Super Admin plan-grant and SQS-sender surfaces take under the businesses prefix.</para>
///
/// <para><strong><c>Imports.Run</c>, the capability the import itself requires.</strong> The alert's
/// only action is "open Import products", which is this capability: a role that may not run the
/// import has nothing to do with being told the import has work waiting. An Operator is therefore
/// refused with the ordinary bodiless <c>403</c>, and sees no alert.</para>
///
/// <para><strong>It makes no Nayax call and no plan gates it.</strong> The whole answer comes from
/// this business's own stored sales and products through the central tenant query filters, so a
/// business whose connection is unusable, or whose plan excludes the sales provider, is answered
/// truthfully about the rows it holds. It carries no amount, price, cost or quantity.</para>
/// </summary>
[ApiController]
[Route("api/catalogue-review/attention")]
[Authorize]
[RequiredScope("access_as_user")]
[RequireCapability(Capability.ImportsRun)]
public sealed class CatalogueReviewAttentionController : ControllerBase
{
    private readonly GetUnimportedProviderProductAttention _attention;

    public CatalogueReviewAttentionController(GetUnimportedProviderProductAttention attention)
    {
        _attention = attention;
    }

    /// <summary>
    /// How many distinct Nayax products this business has sold in the last thirty business calendar
    /// dates without holding a product that carries the provider's id for them, and the most
    /// recently sold few of them by name. A count of zero means there is nothing left to import.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<UnimportedProviderProductAttention>> Get(
        CancellationToken cancellationToken) =>
        Ok(await _attention.Handle(cancellationToken));
}
