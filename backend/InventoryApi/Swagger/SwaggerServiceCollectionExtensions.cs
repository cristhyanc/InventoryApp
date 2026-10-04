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

            // The purchase and supplier-order response bodies are produced by API-owned DTOs since
            // issue #304, but keep the schema ids and the (absent) requiredness the document has
            // always published - see PublishedResponseSchemaContract. Everything else stays on
            // Swashbuckle's default derivation, which this selector falls back to.
            c.CustomSchemaIds(PublishedResponseSchemaContract.SchemaIdSelector(c.SchemaGeneratorOptions.SchemaIdSelector));
            c.SchemaFilter<PublishedResponseSchemaContract.RequirednessFilter>();
        });

        return services;
    }
}
