using Inventory.Application.Costing;
using InventoryApi.Data;
using InventoryApi.Services.Interfaces;

namespace InventoryApi.Services;

public sealed partial class ImportService : IImportService
{
    private readonly AppDbContext _db;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<ImportService> _logger;
    private readonly ICostSale _saleCosting;
    private readonly IRebuildProductCost _inventoryCostRebuild;

    public ImportService(
        AppDbContext db,
        IWebHostEnvironment environment,
        ILogger<ImportService> logger,
        ICostSale saleCosting,
        IRebuildProductCost inventoryCostRebuild)
    {
        _db = db;
        _environment = environment;
        _logger = logger;
        _saleCosting = saleCosting;
        _inventoryCostRebuild = inventoryCostRebuild;
    }

}
