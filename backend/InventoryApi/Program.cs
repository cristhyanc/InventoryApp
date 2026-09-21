using Microsoft.EntityFrameworkCore;
using Inventory.Application;
using Inventory.Application.NayaxFeeSettings;
using Inventory.Application.Reporting.Bookkeeping;
using Inventory.Infrastructure;
using InventoryApi.Adapters.Persistence;
using InventoryApi.Data;
using InventoryApi.Http;
using InventoryApi.Integrations.Nayax;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddApplicationServices();
builder.Services.AddInfrastructureServices();

// Controlled RFC 7807 responses for Nayax upstream failures.
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<NayaxUpstreamExceptionHandler>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "Inventory API",
        Version = "v1",
        Description = "Manage inventory for snacks and drinks, including suppliers, categories, stock adjustments, and receipt uploads."
    });
});

builder.Services.AddDbContext<AppDbContext>(options =>
{
    options.UseLazyLoadingProxies();
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")
        ?? "Data Source=inventory.db");
});

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAngularDevClient", policy =>
    {
        policy.WithOrigins("http://localhost:4200", "https://red-island-0c128c000.7.azurestaticapps.net")
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

builder.Services.Configure<NayaxLynxOptions>(
    builder.Configuration.GetSection(NayaxLynxOptions.SectionName));

builder.Services.AddHttpClient<INayaxLynxClient, NayaxLynxClient>();

// Business services
builder.Services.AddScoped<InventoryApi.Services.Interfaces.IProductService, InventoryApi.Services.ProductService>();
builder.Services.AddScoped<InventoryApi.Services.Interfaces.ICategoryService, InventoryApi.Services.CategoryService>();
builder.Services.AddScoped<InventoryApi.Services.Interfaces.ISupplierService, InventoryApi.Services.SupplierService>();
builder.Services.AddScoped<InventoryApi.Services.Interfaces.IStockService, InventoryApi.Services.StockService>();
builder.Services.AddScoped<InventoryApi.Services.Interfaces.IInventoryCostService, InventoryApi.Services.InventoryCostService>();
builder.Services.AddScoped<InventoryApi.Services.Interfaces.IInventoryCostRebuildService, InventoryApi.Services.InventoryCostRebuildService>();
builder.Services.AddScoped<InventoryApi.Services.Interfaces.IInventoryCostTransitionService, InventoryApi.Services.InventoryCostTransitionService>();
builder.Services.AddScoped<InventoryApi.Services.Interfaces.ISaleCostingService, InventoryApi.Services.SaleCostingService>();
builder.Services.AddScoped<InventoryApi.Services.Interfaces.IReceiptService, InventoryApi.Services.ReceiptService>();
builder.Services.AddScoped<InventoryApi.Services.Interfaces.ISupplierOrderService, InventoryApi.Services.SupplierOrderService>();
builder.Services.AddScoped<InventoryApi.Services.Interfaces.IMachineService, InventoryApi.Services.MachineService>();
builder.Services.AddScoped<InventoryApi.Services.Interfaces.ISiteService, InventoryApi.Services.SiteService>();
builder.Services.AddScoped<InventoryApi.Services.Interfaces.IImportService, InventoryApi.Services.ImportService>();
builder.Services.AddScoped<InventoryApi.Services.Interfaces.IReportingService, InventoryApi.Services.ReportingService>();
builder.Services.AddScoped<InventoryApi.Services.Interfaces.INayaxProcessingFeeService, InventoryApi.Services.NayaxProcessingFeeService>();
builder.Services.AddScoped<InventoryApi.Services.Interfaces.ISiteCommissionService, InventoryApi.Services.SiteCommissionService>();

// Temporary API-owned adapter for the Nayax fee-settings persistence port; see EfNayaxFeeRateStore.
builder.Services.AddScoped<INayaxFeeRateStore, EfNayaxFeeRateStore>();

// Temporary API-owned adapter for the bookkeeping report facts port; see EfBookkeepingReportFactsProvider.
builder.Services.AddScoped<IBookkeepingReportFactsProvider, EfBookkeepingReportFactsProvider>();

var app = builder.Build();

// Apply code-first schema + seed data on startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
    //DbInitializer.Seed(db);
}

// First in the pipeline so exceptions from controllers, services, and the Nayax
// client are all caught.
app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Inventory API v1"));
}

app.UseStaticFiles(); // serves wwwroot/receipts if direct static access is desired
app.UseCors("AllowAngularDevClient");
app.UseAuthorization();
app.MapControllers();

app.Run();
