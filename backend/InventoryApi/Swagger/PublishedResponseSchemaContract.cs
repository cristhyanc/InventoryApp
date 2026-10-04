using InventoryApi.DTOs;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace InventoryApi.Swagger;

/// <summary>
/// Keeps the published OpenAPI schema of the purchase and supplier-order response bodies exactly as
/// the API has always published it, independently of the CLR types that now produce it.
///
/// Issue #304 replaced the serialised EF <c>Purchase</c>/<c>PurchaseItem</c>/<c>SupplierOrder</c>/
/// <c>SupplierOrderLine</c> entities with the API-owned response DTOs in <c>InventoryApi.DTOs</c>.
/// Swashbuckle derives a schema id from the CLR type name and marks a C# <c>required</c> member as
/// <c>required</c> in the schema, so that replacement alone would have renamed four published
/// schemas and added a requiredness declaration the document has never carried. Both are
/// API-contract changes, which issue #304 explicitly excludes, and byte-identical runtime JSON does
/// not make them invisible: a client generated from the document would get differently named models
/// and stricter response validation. The internal names are therefore mapped back onto the
/// published schema ids here, and the requiredness those DTOs declare for the response mappers'
/// benefit is kept out of the published document.
///
/// This pin is deliberately narrow: it names four types explicitly and leaves every other schema on
/// Swashbuckle's default derivation. If an endpoint ever publishes the EF entity a pinned id belongs
/// to, Swashbuckle fails document generation with a duplicate-schema-id error rather than silently
/// renaming one of them, and <c>InventoryApi.Tests.Swagger.PublishedResponseSchemaContractTests</c>
/// is where that surfaces.
/// </summary>
internal static class PublishedResponseSchemaContract
{
    /// <summary>
    /// API-owned response type to the schema id the published document carries for it. The ids are
    /// the ones issue #127 settled on for the purchase endpoints and the ones the supplier-order
    /// endpoints have published since they existed - not the CLR names of the types above.
    /// </summary>
    private static readonly Dictionary<Type, string> PinnedSchemaIds = new()
    {
        [typeof(PurchaseResponse)] = "Purchase",
        [typeof(PurchaseItemResponse)] = "PurchaseItem",
        [typeof(SupplierOrderResponse)] = "SupplierOrder",
        [typeof(SupplierOrderLineResponse)] = "SupplierOrderLine",
    };

    /// <summary>
    /// Wraps Swashbuckle's own schema-id selector so that only the pinned types are redirected and
    /// every other type keeps the default CLR-name derivation.
    /// </summary>
    internal static Func<Type, string> SchemaIdSelector(Func<Type, string> defaultSelector) =>
        type => PinnedSchemaIds.TryGetValue(type, out var pinnedId) ? pinnedId : defaultSelector(type);

    /// <summary>
    /// Drops the <c>required</c> declaration Swashbuckle derives from the pinned response types'
    /// C# <c>required</c> members, which the document did not carry when EF entities filled these
    /// schemas. The members stay <c>required</c> in C#, where they stop a response mapper from
    /// forgetting a field at compile time; only the published description is held still.
    /// </summary>
    internal sealed class RequirednessFilter : ISchemaFilter
    {
        public void Apply(OpenApiSchema schema, SchemaFilterContext context)
        {
            if (PinnedSchemaIds.ContainsKey(context.Type))
            {
                schema.Required.Clear();
            }
        }
    }
}
