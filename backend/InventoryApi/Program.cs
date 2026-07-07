using Microsoft.EntityFrameworkCore;
using InventoryApi.Data;
using InventoryApi.Integrations.Nayax;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
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
        policy.WithOrigins("http://localhost:4200")
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

builder.Services.Configure<NayaxLynxOptions>(
    builder.Configuration.GetSection(NayaxLynxOptions.SectionName));

builder.Services.AddHttpClient<INayaxLynxClient, NayaxLynxClient>();

var app = builder.Build();

// Apply code-first schema + seed data on startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
    //DbInitializer.Seed(db);
}

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
