namespace InventoryApi.Swagger;

/// <summary>
/// The single registration of the published OpenAPI document, so that the composition root and
/// the contract regression tests generate the document from exactly the same configuration.
/// </summary>
public static class SwaggerServiceCollectionExtensions
{
    public static IServiceCollection AddInventoryApiSwagger(this IServiceCollection services)
    {
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(c =>
        {
            c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
            {
                Title = "Inventory API",
                Version = "v1",
                Description = "Manage inventory for snacks and drinks, including suppliers, categories, stock adjustments, and purchase uploads."
            });
        });

        return services;
    }
}
