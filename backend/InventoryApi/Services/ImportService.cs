using InventoryApi.Data;
using InventoryApi.Integrations.Nayax;
using InventoryApi.Services.Interfaces;
using Microsoft.AspNetCore.Http;

namespace InventoryApi.Services;

public sealed partial class ImportService : IImportService
{
    private readonly AppDbContext _db;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<ImportService> _logger;
    private readonly INayaxLynxClient _nayaxLynxClient;
    private readonly ISaleCostingService _saleCosting;

    public ImportService(
        AppDbContext db,
        IWebHostEnvironment environment,
        ILogger<ImportService> logger,
        INayaxLynxClient nayaxLynxClient,
        ISaleCostingService saleCosting)
    {
        _db = db;
        _environment = environment;
        _logger = logger;
        _nayaxLynxClient = nayaxLynxClient;
        _saleCosting = saleCosting;
    }

}
