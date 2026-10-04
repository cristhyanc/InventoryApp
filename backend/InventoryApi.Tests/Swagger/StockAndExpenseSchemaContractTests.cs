#nullable enable
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Xunit;

namespace InventoryApi.Tests.Swagger;

/// <summary>
/// Locks the published OpenAPI description of the stock and operating-expense endpoints after issue
/// #305 pointed them at API-owned contracts.
///
/// Byte-identical runtime JSON is not enough on its own: Swashbuckle derives a schema id from the
/// CLR type name, so swapping the type behind a response or a bound enum renames or repoints a
/// client-visible component. Two separate rules are pinned here.
///
/// <para><b>The expense category is the same component it always was.</b> The DTOs now carry
/// <c>InventoryApi.DTOs.OperatingExpenseCategory</c> instead of the identically named persistence
/// enum, which derives the same id with the same integer values, and the persistence enum has left
/// the document entirely because nothing publishes the <c>OperatingExpense</c> entity. One
/// component, same name, same values, same references.</para>
///
/// <para><b>The stock-adjustment vocabulary could not move, and this is why.</b> The published
/// document still reaches <c>InventoryApi.Models.StockAdjustmentReason</c>/<c>StockAdjustmentSource</c>
/// through the legacy <c>Product</c> component that
/// <c>InventoryApi.Swagger.PublishedResponseSchemaContract</c> regenerates for the pinned
/// purchase/supplier-order schemas: <c>Product</c> references <c>StockAdjustment</c>, which
/// references both enums. An API-owned enum of the same simple name therefore cannot exist while
/// that reference does - Swashbuckle fails document generation with
/// <c>Can't use schemaId "$StockAdjustmentReason" ...</c> - so the stock DTOs keep naming the
/// persistence enums, and the response and the legacy entity component share one published
/// vocabulary. These tests pin that reachability, so whoever retires the legacy <c>Product</c> pin
/// (issues #153/#154) is told here that the enums can move with it.</para>
/// </summary>
public class StockAndExpenseSchemaContractTests
{
    private const string StockPath = "/api/products/{productId}/stock";
    private const string OperatingExpensesPath = "/api/operating-expenses";

    [Fact]
    public void Stock_operations_publish_the_api_owned_response_schema()
    {
        var document = ApiContractTestHost.GetSwaggerDocument();

        var referenced = ReferencedByOperationsUnder(document, StockPath);

        Assert.Contains("ProductStockAdjustmentResponse", referenced);
        Assert.DoesNotContain("StockAdjustment", referenced);
    }

    /// <summary>
    /// The legacy <c>StockAdjustment</c> component itself stays published, because the pinned
    /// <c>Product</c> component still references it; no stock operation points at it any more.
    /// </summary>
    [Fact]
    public void Legacy_stock_adjustment_component_stays_published_for_the_pinned_product_component()
    {
        var document = ApiContractTestHost.GetSwaggerDocument();

        Assert.Contains("StockAdjustment", document.Components.Schemas.Keys);
        Assert.Equal(
            "StockAdjustment",
            document.Components.Schemas["Product"].Properties["stockAdjustments"].Items.Reference?.Id);
    }

    [Fact]
    public void Stock_adjustment_reason_is_published_once_with_the_same_numeric_values()
    {
        var document = ApiContractTestHost.GetSwaggerDocument();

        AssertIntegerEnum(document, "StockAdjustmentReason", [0, 1, 2, 3, 4, 5]);

        // One vocabulary, reached from both the API-owned response and the legacy entity component.
        Assert.Equal(
            "StockAdjustmentReason",
            document.Components.Schemas["ProductStockAdjustmentResponse"].Properties["reason"].Reference?.Id);
        Assert.Equal(
            "StockAdjustmentReason",
            document.Components.Schemas["StockAdjustment"].Properties["reason"].Reference?.Id);
    }

    [Fact]
    public void Stock_adjustment_source_is_published_once_with_the_same_numeric_values()
    {
        var document = ApiContractTestHost.GetSwaggerDocument();

        AssertIntegerEnum(document, "StockAdjustmentSource", [0, 1]);

        Assert.Equal(
            "StockAdjustmentSource",
            document.Components.Schemas["ProductStockAdjustmentResponse"].Properties["source"].Reference?.Id);
        Assert.Equal(
            "StockAdjustmentSource",
            document.Components.Schemas["StockAdjustment"].Properties["source"].Reference?.Id);
    }

    [Fact]
    public void Operating_expense_category_is_published_once_with_the_same_numeric_values()
    {
        var document = ApiContractTestHost.GetSwaggerDocument();

        AssertIntegerEnum(document, "OperatingExpenseCategory", [0, 1, 2, 3, 4, 5, 6, 7, 8]);
    }

    /// <summary>
    /// Every operating-expense schema and operation that describes a category still points at the
    /// one <c>OperatingExpenseCategory</c> component - the request body, the single-record response
    /// and the listing row - and the listing's category filter is still described as that component
    /// rather than an inlined copy under a new name.
    /// </summary>
    [Theory]
    [InlineData("OperatingExpenseDto")]
    [InlineData("OperatingExpenseResponse")]
    [InlineData("OperatingExpenseReportRowDto")]
    public void Operating_expense_schema_references_the_published_category_component(string schemaId)
    {
        var document = ApiContractTestHost.GetSwaggerDocument();

        Assert.Equal(
            "OperatingExpenseCategory",
            document.Components.Schemas[schemaId].Properties["category"].Reference?.Id);
    }

    [Fact]
    public void Operating_expense_listing_still_filters_by_the_published_category_component()
    {
        var document = ApiContractTestHost.GetSwaggerDocument();

        var categoryParameter = Assert.Single(
            document.Paths[OperatingExpensesPath].Operations[OperationType.Get].Parameters
                .Where(parameter => parameter.Name == "category"));

        Assert.Equal("OperatingExpenseCategory", categoryParameter.Schema.Reference?.Id);
    }

    [Fact]
    public void Operating_expense_operations_publish_the_api_owned_response_schemas()
    {
        var document = ApiContractTestHost.GetSwaggerDocument();

        var referenced = ReferencedByOperationsUnder(document, OperatingExpensesPath);

        Assert.Contains("OperatingExpenseResponse", referenced);
        Assert.Contains("OperatingExpenseReportRowDto", referenced);
        Assert.DoesNotContain("OperatingExpense", referenced);
        Assert.DoesNotContain("OperatingExpense", document.Components.Schemas.Keys);
    }

    private static void AssertIntegerEnum(OpenApiDocument document, string schemaId, int[] expectedValues)
    {
        var schema = document.Components.Schemas[schemaId];

        Assert.Equal("integer", schema.Type);
        Assert.Equal("int32", schema.Format);
        Assert.Equal(
            expectedValues,
            schema.Enum.OfType<OpenApiInteger>().Select(value => value.Value).ToArray());
    }

    private static IReadOnlyList<string> ReferencedByOperationsUnder(OpenApiDocument document, string pathPrefix) =>
        document.Paths
            .Where(path => path.Key.StartsWith(pathPrefix, StringComparison.Ordinal))
            .SelectMany(path => path.Value.Operations.Values)
            .SelectMany(operation => operation.Responses.Values)
            .SelectMany(response => response.Content.Values)
            .Select(media => media.Schema)
            .SelectMany(schema => ReferencedSchemaIds(schema).Concat(ReferencedSchemaIds(schema?.Items)))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static IEnumerable<string> ReferencedSchemaIds(OpenApiSchema? schema)
    {
        if (schema is null) yield break;
        if (schema.Reference?.Id is { } id) yield return id;
        if (schema.Items?.Reference?.Id is { } itemId) yield return itemId;
    }
}
