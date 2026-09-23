using InventoryApi.Controllers;
using InventoryApi.DTOs;
using InventoryApi.Models;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Controllers;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace InventoryApi.Swagger;

/// <summary>
/// Pins the generated OpenAPI document to the identifiers it published before the internal
/// Receipt-to-Purchase rename (issue #60).
///
/// Swashbuckle derives schema ids from CLR type names and operation tags from the controller
/// name, so renaming <c>Receipt</c> to <see cref="Purchase"/> and <c>ReceiptsController</c> to
/// <see cref="PurchasesController"/> would silently rename <c>Receipt</c>, <c>ReceiptItem</c>,
/// <c>ReceiptResponseDto</c>, <c>ReceiptValidationDto</c> and the <c>Receipts</c> tag in the
/// published contract, breaking generated clients even though the routes and JSON keys are
/// unchanged. This is the compatibility boundary that keeps the published names stable while
/// the code keeps Purchase terminology; it maps only the renamed types and the one renamed
/// controller, and defers to Swashbuckle's own defaults for everything else.
/// </summary>
public static class LegacyOpenApiCompatibility
{
    /// <summary>The OpenAPI tag the purchase ("receipts") endpoints were published under.</summary>
    public const string LegacyPurchasesTag = "Receipts";

    /// <summary>
    /// Renamed CLR types mapped back to the schema id each one was published as. Types that never
    /// reach the document are harmless: the selector is only consulted for schemas it generates.
    /// </summary>
    public static IReadOnlyDictionary<Type, string> LegacySchemaIds { get; } = new Dictionary<Type, string>
    {
        [typeof(Purchase)] = "Receipt",
        [typeof(PurchaseItem)] = "ReceiptItem",
        [typeof(PurchaseItemDto)] = "ReceiptItemDto",
        [typeof(PurchaseCreateMetaDto)] = "ReceiptCreateMetaDto",
        [typeof(PurchaseValidationDto)] = "ReceiptValidationDto",
        [typeof(PurchaseResponseDto)] = "ReceiptResponseDto",
    };

    /// <summary>
    /// Applies the legacy schema ids and the legacy controller tag, preserving Swashbuckle's
    /// default behaviour for every type and controller that was not renamed.
    /// </summary>
    public static void UseLegacyReceiptNames(this SwaggerGenOptions options)
    {
        var defaultSchemaIdSelector = options.SchemaGeneratorOptions.SchemaIdSelector;
        options.CustomSchemaIds(type => LegacySchemaIds.TryGetValue(type, out var legacyId)
            ? legacyId
            : defaultSchemaIdSelector?.Invoke(type) ?? type.Name);

        var defaultTagsSelector = options.SwaggerGeneratorOptions.TagsSelector;
        options.TagActionsBy(apiDescription => IsPurchasesController(apiDescription)
            ? new[] { LegacyPurchasesTag }
            : defaultTagsSelector?.Invoke(apiDescription)
                ?? new[] { apiDescription.ActionDescriptor.RouteValues["controller"] ?? string.Empty });
    }

    private static bool IsPurchasesController(ApiDescription apiDescription) =>
        apiDescription.ActionDescriptor is ControllerActionDescriptor descriptor &&
        descriptor.ControllerTypeInfo.AsType() == typeof(PurchasesController);
}
