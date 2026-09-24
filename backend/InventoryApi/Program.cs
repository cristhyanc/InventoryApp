using Microsoft.EntityFrameworkCore;
using Inventory.Application;
using Inventory.Application.NayaxFeeSettings;
using Inventory.Application.Reporting.Bookkeeping;
using Inventory.Application.Reporting.Dashboard;
using Inventory.Application.Reporting.Daily;
using Inventory.Application.Reporting.Gst;
using Inventory.Application.Reporting.MachineProfitability;
using Inventory.Application.Reporting.ProductProfitability;
using Inventory.Application.Reporting.Reconciliation;
using Inventory.Application.Reporting.Transactions;
using Inventory.Application.Tenancy;
using Inventory.Infrastructure;
using InventoryApi.Adapters.Persistence;
using InventoryApi.Bootstrap;
using InventoryApi.Auth;
using InventoryApi.Data;
using InventoryApi.Http;
using InventoryApi.Integrations.Nayax;
using InventoryApi.Swagger;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Identity.Web;

// The business bootstrap is a separate, human-invoked path (issue #64, checkpoint 3). It is
// checked before the web host is built so that starting the API and backfilling ownership can
// never be the same action: a deployment starts the API and does not reach this branch.
if (BusinessBootstrapCommand.Matches(args))
{
    return await BusinessBootstrapCommand.RunAsync(args, CancellationToken.None);
}

// Applying schema migrations is likewise a human-invoked command, not something a deployment
// performs. Normal startup below applies nothing outside Development.
if (DatabaseMigrationCommand.Matches(args))
{
    return await DatabaseMigrationCommand.RunAsync(args, CancellationToken.None);
}

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));

builder.Services.AddAuthorization();

// Required by EntraActorIdentityAccessor, which reads the current request's ClaimsPrincipal.
builder.Services.AddHttpContextAccessor();

builder.Services.AddControllers();
builder.Services.AddApplicationServices();
builder.Services.AddInfrastructureServices();

// Controlled RFC 7807 responses for Nayax upstream failures.
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<NayaxUpstreamExceptionHandler>();
builder.Services.AddInventoryApiSwagger();

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
builder.Services.AddScoped<InventoryApi.Services.Interfaces.IPurchaseService, InventoryApi.Services.PurchaseService>();
builder.Services.AddScoped<InventoryApi.Services.Interfaces.ISupplierOrderService, InventoryApi.Services.SupplierOrderService>();
builder.Services.AddScoped<InventoryApi.Services.Interfaces.IMachineService, InventoryApi.Services.MachineService>();
builder.Services.AddScoped<InventoryApi.Services.Interfaces.ISiteService, InventoryApi.Services.SiteService>();
builder.Services.AddScoped<InventoryApi.Services.Interfaces.IImportService, InventoryApi.Services.ImportService>();
builder.Services.AddScoped<InventoryApi.Services.Interfaces.INayaxProcessingFeeService, InventoryApi.Services.NayaxProcessingFeeService>();
builder.Services.AddScoped<InventoryApi.Services.Interfaces.ISiteCommissionService, InventoryApi.Services.SiteCommissionService>();

// Tenancy (issue #64). Claims parsing stays at this boundary: EntraActorIdentityAccessor is the
// only implementation of the Application's actor port, and the current-business abstraction
// itself (ICurrentBusinessProvider) is registered by AddApplicationServices().
builder.Services.AddScoped<IAuthenticatedActorAccessor, EntraActorIdentityAccessor>();

// The per-request current business, published by BusinessScopeMiddleware and read by
// AppDbContext's query filters and SaveChanges enforcement. Registered as the concrete type as
// well, because only the middleware may resolve it; everything else consumes the read-only port.
builder.Services.AddScoped<BusinessScope>();
builder.Services.AddScoped<IBusinessScope>(sp => sp.GetRequiredService<BusinessScope>());

// Temporary API-owned adapter for the business membership port; see EfBusinessMembershipStore.
builder.Services.AddScoped<IBusinessMembershipStore, EfBusinessMembershipStore>();

// Temporary API-owned adapter for the Nayax fee-settings persistence port; see EfNayaxFeeRateStore.
builder.Services.AddScoped<INayaxFeeRateStore, EfNayaxFeeRateStore>();

// Temporary API-owned adapter for the bookkeeping report facts port; see EfBookkeepingReportFactsProvider.
builder.Services.AddScoped<IBookkeepingReportFactsProvider, EfBookkeepingReportFactsProvider>();

// Temporary API-owned adapter for the daily report facts port; see EfDailyReportFactsProvider.
builder.Services.AddScoped<IDailyReportFactsProvider, EfDailyReportFactsProvider>();

// Temporary API-owned adapter for the reconciliation report facts port; see EfReconciliationReportFactsProvider.
builder.Services.AddScoped<IReconciliationReportFactsProvider, EfReconciliationReportFactsProvider>();

// Temporary API-owned adapter for the machine profitability report facts port; see EfMachineProfitabilityReportFactsProvider.
builder.Services.AddScoped<IMachineProfitabilityReportFactsProvider, EfMachineProfitabilityReportFactsProvider>();

// Temporary API-owned adapter for the product profitability report facts port; see EfProductProfitabilityReportFactsProvider.
builder.Services.AddScoped<IProductProfitabilityReportFactsProvider, EfProductProfitabilityReportFactsProvider>();

// Temporary API-owned adapter for the GST accounting-aid report facts port; see EfGstReportFactsProvider.
builder.Services.AddScoped<IGstReportFactsProvider, EfGstReportFactsProvider>();

// Temporary API-owned adapter for the dashboard report facts port; see EfDashboardReportFactsProvider.
builder.Services.AddScoped<IDashboardReportFactsProvider, EfDashboardReportFactsProvider>();

// Temporary API-owned adapter for the transaction sales report facts port; see EfTransactionSalesReportFactsProvider.
builder.Services.AddScoped<ITransactionSalesReportFactsProvider, EfTransactionSalesReportFactsProvider>();

var app = builder.Build();

// Schema handling at startup (issue #64). Development applies migrations automatically; every
// other environment applies nothing and fails closed if any are pending, because the tenancy
// migrations - including the NayaxSales table rebuild - must be applied by a human under review.
// See DatabaseSchemaStartup and docs/tenant-rollout.md.
//
// Schema migrations never assign tenant ownership or perform the business backfill - that is
// exclusively the human-invoked `bootstrap-business` command. Some of them do rebuild tables and
// copy persisted rows (the NayaxSales re-key), which is a further reason production migration is
// human-controlled rather than a deployment side effect.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();

    DatabaseSchemaStartup.EnsureSchema(db, app.Environment, app.Configuration, loggerFactory);
    //DbInitializer.Seed(db);

    TenantOwnershipReadiness.Report(db, loggerFactory);
}

// First in the pipeline so exceptions from controllers, services, and the Nayax
// client are all caught.
app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Inventory API v1"));
}

// No static-file middleware: the API serves no public assets (the Angular application is a
// separate Azure Static Web App), and static-file middleware does not run controller
// authorization. Uploaded purchase and operating-expense documents are stored outside the
// web root (see ProtectedFileStorage) and are only readable through the [Authorize]d
// endpoints, so no business document has an anonymous URL.
app.UseCors("AllowAngularDevClient");
app.UseAuthentication();
app.UseAuthorization();

// After authentication, so the caller's claims exist, and before the endpoint, so an
// authenticated caller with no business membership is refused before any action reads data.
app.UseMiddleware<BusinessScopeMiddleware>();

app.MapControllers();

app.Run();

return 0;

// Exposed so InventoryApi.Tests can host the API with WebApplicationFactory<Program>.
public partial class Program;
