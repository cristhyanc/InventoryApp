using Inventory.Application.Access;
using Inventory.Application.Sites;
using InventoryApi.Adapters.Mapping;
using InventoryApi.Auth;
using InventoryApi.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

/// <summary>
/// The sites a machine fleet stands at, and what each site's machines hold. Read-only, and part of
/// the same day-to-day inventory work as the machines themselves, so it requires
/// <c>Inventory.Operate</c> (issue #525). The money side of a site - its commission agreements and
/// payments - is <c>SiteCommissions.Manage</c> on <c>SiteCommissionsController</c> and is issue
/// #526's to classify.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
[RequiredScope("access_as_user")]
[RequireCapability(Capability.InventoryOperate)]
public class SitesController : ControllerBase
{
    private readonly GetSiteSummaries _getSiteSummaries;
    private readonly GetSiteProducts _getSiteProducts;

    public SitesController(GetSiteSummaries getSiteSummaries, GetSiteProducts getSiteProducts)
    {
        _getSiteSummaries = getSiteSummaries;
        _getSiteProducts = getSiteProducts;
    }

    [HttpGet]
    public async Task<ActionResult<List<SiteSummaryDto>>> GetAll()
    {
        var summaries = await _getSiteSummaries.Handle(CancellationToken.None);
        return Ok(summaries.Select(SiteResponseMapper.ToDto).ToList());
    }

    [HttpGet("{id:long}/products")]
    public async Task<ActionResult<List<SiteProductDto>>> GetProducts(long id)
    {
        var products = await _getSiteProducts.Handle(id, CancellationToken.None);
        return Ok(products.Select(SiteResponseMapper.ToDto).ToList());
    }
}
