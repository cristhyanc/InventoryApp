using System.Globalization;
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
/// Swashbuckle derives a schema id from the CLR type name, marks a C# <c>required</c> member as
/// <c>required</c> in the schema, and names a nested object's schema after that nested member's CLR
/// type, so that replacement alone would have renamed four published schemas, added a requiredness
/// declaration the document has never carried, and repointed the nested <c>product</c>/
/// <c>supplier</c> objects at different components. All three are API-contract changes, which issue
/// #304 explicitly excludes, and byte-identical runtime JSON does not make them invisible: a client
/// generated from the document would get differently named models, differently named nested models
/// and stricter response validation. This type is the compatibility boundary that holds the
/// published description still while the code behind it moves.
///
/// Issue #305 added the stock endpoints to the same boundary by the other available mechanism. The
/// stock history and adjust actions return the API-owned <c>ProductStockAdjustmentResponse</c>,
/// whose own schema id the product endpoints already publish for a movement in a product's history
/// (issue #303), so pinning the id was not available here: two published identities for one CLR type
/// cannot come from one schema-id selector. Their published <em>response</em> is substituted instead
/// - see <see cref="PublishedResponseFilter"/> - so the two operations keep describing
/// <c>#/components/schemas/StockAdjustment</c>, exactly as the base branch published them.
///
/// This is deliberately narrow. It names four response types and one substituted response
/// explicitly; every other schema stays on Swashbuckle's default derivation, and the product and
/// supplier endpoints keep publishing their own <c>ProductResponse</c>/<c>SupplierResponse</c>
/// contracts (issues #302/#303) untouched. If an endpoint ever publishes the EF entity a pinned id
/// belongs to, Swashbuckle fails document generation with a duplicate-schema-id error rather than
/// silently renaming one of them, and
/// <c>InventoryApi.Tests.Swagger.PublishedResponseSchemaContractTests</c> is where that surfaces.
///
/// This is where the API project names <c>InventoryApi.Models</c> for presentation purposes
/// deliberately: the controllers and the use cases stay free of it, and the entity types are reached
/// here solely to regenerate the legacy component shapes the published document references. The one
/// other presentation use left is the stock-adjustment reason/source vocabulary that this boundary
/// itself keeps published, which <c>InventoryApi.DTOs.StockAdjustmentDto</c> and
/// <c>InventoryApi.DTOs.ProductStockAdjustmentResponse</c> carry as a temporary compatibility
/// exception documented on those types and in docs/architecture.md § InventoryApi.
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
    /// Inside a pinned schema only, the schema id of a nested API-owned object and the legacy
    /// entity whose component the published document references in its place. A purchase item's and
    /// a supplier-order line's <c>product</c> have always pointed at <c>#/components/schemas/Product</c>
    /// and a purchase's and a supplier order's <c>supplier</c> at <c>#/components/schemas/Supplier</c>;
    /// the mappers now build <c>ProductResponse</c>/<c>SupplierResponse</c> instead, whose own
    /// published contracts belong to the product and supplier endpoints and must not move.
    ///
    /// The replacement regenerates the entity's schema through Swashbuckle's own generator rather
    /// than writing a reference string, so the referenced component is registered with its complete
    /// base shape (including its own nested <c>category</c>/<c>supplier</c>/<c>stockAdjustments</c>
    /// references) even if no other endpoint happens to publish that entity.
    /// </summary>
    private static readonly Dictionary<string, Type> LegacyNestedSchemaTypes = new(StringComparer.Ordinal)
    {
        ["ProductResponse"] = typeof(Models.Product),
        ["SupplierResponse"] = typeof(Models.Supplier),
    };

    /// <summary>
    /// API-owned response type to the legacy entity whose published schema the document describes
    /// for an operation that returns it. The stock history and adjust endpoints have always
    /// described <c>#/components/schemas/StockAdjustment</c>; issue #305 pointed them at the
    /// API-owned <c>ProductStockAdjustmentResponse</c> so the controller stops naming the
    /// persistence model, and the published description stays where it was.
    ///
    /// Unlike <see cref="PinnedSchemaIds"/> this cannot be a schema-id redirect:
    /// <c>ProductStockAdjustmentResponse</c> is also published under its own id, as the item type of
    /// <c>ProductResponse.stockAdjustments</c> (issue #303), and one CLR type cannot carry two
    /// schema ids. The substitution is therefore scoped to the operation's response, where the
    /// published shape and the serialised shape are proved equal by
    /// <c>InventoryApi.Tests.Swagger.StockAndExpenseSchemaContractTests</c> (the two components are
    /// schema-identical) and by
    /// <c>InventoryApi.Tests.DTOs.StockAdjustmentResponseJsonContractTests</c> (the payload is
    /// byte-identical).
    /// </summary>
    private static readonly Dictionary<Type, Type> SubstitutedResponseTypes = new()
    {
        [typeof(ProductStockAdjustmentResponse)] = typeof(Models.StockAdjustment),
    };

    /// <summary>
    /// Wraps Swashbuckle's own schema-id selector so that only the pinned types are redirected and
    /// every other type keeps the default CLR-name derivation.
    /// </summary>
    internal static Func<Type, string> SchemaIdSelector(Func<Type, string> defaultSelector) =>
        type => PinnedSchemaIds.TryGetValue(type, out var pinnedId) ? pinnedId : defaultSelector(type);

    /// <summary>
    /// Restores the two parts of a pinned schema that the CLR types behind it would otherwise have
    /// changed: the <c>required</c> declaration Swashbuckle derives from their C# <c>required</c>
    /// members, which the document did not carry when EF entities filled these schemas, and the
    /// nested <c>product</c>/<c>supplier</c> references. The C# members stay <c>required</c>, where
    /// they stop a response mapper from forgetting a field at compile time; only the published
    /// description is held still.
    /// </summary>
    internal sealed class PublishedShapeFilter : ISchemaFilter
    {
        public void Apply(OpenApiSchema schema, SchemaFilterContext context)
        {
            if (!PinnedSchemaIds.ContainsKey(context.Type))
            {
                return;
            }

            schema.Required.Clear();

            foreach (var property in schema.Properties.ToList())
            {
                if (property.Value.Reference?.Id is { } referencedId &&
                    LegacyNestedSchemaTypes.TryGetValue(referencedId, out var legacyType))
                {
                    schema.Properties[property.Key] =
                        context.SchemaGenerator.GenerateSchema(legacyType, context.SchemaRepository);
                }
            }
        }
    }

    /// <summary>
    /// Describes an operation's response with the schema of the legacy type the document published
    /// for it, for the responses named in <see cref="SubstitutedResponseTypes"/> only.
    ///
    /// The schema is regenerated from that legacy type through Swashbuckle's own generator, with the
    /// same call Swashbuckle makes for a declared response type, so the published media type comes
    /// out identical to the base branch's - including a collection response, whose element type is
    /// substituted inside the declared <c>IEnumerable&lt;T&gt;</c> rather than patched afterwards -
    /// and the referenced component is registered with its complete shape instead of dangling.
    /// Nothing else about the operation is touched: status codes, content types, parameters and the
    /// request body stay as MVC described them.
    /// </summary>
    internal sealed class PublishedResponseFilter : IOperationFilter
    {
        public void Apply(OpenApiOperation operation, OperationFilterContext context)
        {
            foreach (var responseType in context.ApiDescription.SupportedResponseTypes)
            {
                if (PublishedTypeFor(responseType.Type) is not { } publishedType)
                {
                    continue;
                }

                var statusCode = responseType.StatusCode.ToString(CultureInfo.InvariantCulture);
                if (!operation.Responses.TryGetValue(statusCode, out var response))
                {
                    continue;
                }

                foreach (var mediaType in response.Content.Values)
                {
                    mediaType.Schema =
                        context.SchemaGenerator.GenerateSchema(publishedType, context.SchemaRepository);
                }
            }
        }

        /// <summary>
        /// The legacy type whose schema the document publishes for <paramref name="responseType"/>,
        /// or <c>null</c> when the response is not substituted. A substituted type nested in a
        /// generic response - <c>IEnumerable&lt;ProductStockAdjustmentResponse&gt;</c> for the
        /// history endpoint - is replaced inside the same generic type, so the array wrapper the
        /// document carries is the one MVC's declared type produces.
        /// </summary>
        private static Type? PublishedTypeFor(Type? responseType)
        {
            if (responseType is null)
            {
                return null;
            }

            if (SubstitutedResponseTypes.TryGetValue(responseType, out var publishedType))
            {
                return publishedType;
            }

            if (!responseType.IsGenericType)
            {
                return null;
            }

            var arguments = responseType.GetGenericArguments();
            var substituted = arguments
                .Select(argument => PublishedTypeFor(argument) ?? argument)
                .ToArray();

            return substituted.SequenceEqual(arguments)
                ? null
                : responseType.GetGenericTypeDefinition().MakeGenericType(substituted);
        }
    }
}
