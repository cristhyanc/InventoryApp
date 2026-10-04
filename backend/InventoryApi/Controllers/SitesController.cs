using Inventory.Application.Sites;
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
