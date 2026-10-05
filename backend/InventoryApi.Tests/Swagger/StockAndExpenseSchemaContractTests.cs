#nullable enable
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Extensions;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;
using Xunit;

namespace InventoryApi.Tests.Swagger;

/// <summary>
/// Locks the published OpenAPI description of the stock and operating-expense endpoints after issue
/// #305 pointed them at API-owned contracts.
///
/// Byte-identical runtime JSON is not enough on its own: Swashbuckle derives a schema id from the
/// CLR type name, so swapping the type behind a response or a bound enum renames or repoints a
/// client-visible component, and a generated client gets a differently named model. Issue #305
/// excludes API-contract changes and requires the published document to stay as
/// <c>develop</c> published it, so the generated document - not only the payload - is compared here
/// against the base branch.
///
/// <para><b>The stock operations publish exactly the base contract.</b> The actions return the
/// API-owned <c>InventoryApi.DTOs.ProductStockAdjustmentResponse</c>, and
/// <c>InventoryApi.Swagger.PublishedResponseSchemaContract</c> keeps their published response
/// pointing at <c>#/components/schemas/StockAdjustment</c>, the component the endpoints have always
/// described. The baselines in <see cref="BaseStockOperations"/>/<see cref="BaseStockComponents"/>
/// are the base branch's generated operations and components, captured from the real document
/// generation with the base branch's declared response types, and they are compared literally - no
/// substitution is applied to excuse a difference, so any schema-id, <c>$ref</c>, property, status
/// code, content type or request-body change fails here.</para>
///
/// <para><b>The expense category is the same component it always was.</b> The DTOs now carry
/// <c>InventoryApi.DTOs.OperatingExpenseCategory</c> instead of the identically named persistence
/// enum, which derives the same id with the same integer values, and the persistence enum has left
/// the document entirely because nothing publishes the <c>OperatingExpense</c> entity. One
/// component, same name, same values, same references.</para>
///
/// <para><b>The stock-adjustment vocabulary could not move, and this is why.</b> The published
/// document still reaches <c>InventoryApi.Models.StockAdjustmentReason</c>/<c>StockAdjustmentSource</c>
/// through the pinned <c>StockAdjustment</c> response component itself and through the legacy
/// <c>Product</c> component that the same compatibility boundary regenerates for the pinned
/// purchase/supplier-order schemas. An API-owned enum of the same simple name therefore cannot exist
/// while those references do - Swashbuckle fails document generation with
/// <c>Can't use schemaId "$StockAdjustmentReason" ...</c> - so the stock request DTO and the stock
/// response DTO keep naming the persistence enums, and the request body, the response and the legacy
/// entity component share one published vocabulary. These tests pin that reachability, so whoever
/// retires the pinned legacy components (issues #153/#154) is told here that the enums can move with
/// them.</para>
/// </summary>
public class StockAndExpenseSchemaContractTests
{
    private const string StockPath = "/api/products/{productId}/stock";
    private const string RestockCostSuggestionPath = "/api/products/{productId}/stock/restock-cost-suggestion";
    private const string OperatingExpensesPath = "/api/operating-expenses";

    /// <summary>
    /// The whole published operation, compared with the base branch's: the response schema
    /// reference a generated client binds to, and with it the status codes, content types, path
    /// parameters and request body. The <c>restock-cost-suggestion</c> operation is included because
    /// issue #305 must leave it alone, and the comparison is what proves it did.
    /// </summary>
    [Theory]
    [InlineData(StockPath, OperationType.Get)]
    [InlineData(StockPath, OperationType.Post)]
    [InlineData(RestockCostSuggestionPath, OperationType.Get)]
    public void Stock_operation_matches_the_base_contract(string path, OperationType method)
    {
        var document = ApiContractTestHost.GetSwaggerDocument();

        var published = Normalize(
            document.Paths[path].Operations[method].SerializeAsJson(OpenApiSpecVersion.OpenApi3_0));

        Assert.Equal(Normalize(BaseStockOperations[$"{method} {path}"]), published);
    }

    /// <summary>
    /// Every component the stock operations reach, compared whole with the base branch's: the
    /// pinned <c>StockAdjustment</c> response shape, the <c>StockAdjustmentDto</c> request body -
    /// whose <c>reason</c> is still the persistence enum's component, the request-side half of the
    /// compatibility exception - the two enum vocabularies, and the untouched
    /// <c>RestockCostSuggestionDto</c>.
    /// </summary>
    [Theory]
    [InlineData("StockAdjustment")]
    [InlineData("StockAdjustmentDto")]
    [InlineData("StockAdjustmentReason")]
    [InlineData("StockAdjustmentSource")]
    [InlineData("RestockCostSuggestionDto")]
    public void Stock_component_matches_the_base_contract(string schemaId)
    {
        var document = ApiContractTestHost.GetSwaggerDocument();

        Assert.True(
            document.Components.Schemas.ContainsKey(schemaId),
            $"Schema '{schemaId}' is missing. Published schemas: " +
            string.Join(", ", document.Components.Schemas.Keys.Order()));

        var published = Normalize(
            document.Components.Schemas[schemaId].SerializeAsJson(OpenApiSpecVersion.OpenApi3_0));

        Assert.Equal(Normalize(BaseStockComponents[schemaId]), published);
    }

    /// <summary>
    /// The published response reference, named on its own rather than only inside the whole-operation
    /// comparison, because it is the part the response-type swap changes most quietly: the actions
    /// return <c>ProductStockAdjustmentResponse</c>, and without the compatibility boundary
    /// Swashbuckle would publish that id here instead of the one clients read.
    /// </summary>
    [Fact]
    public void Stock_operations_publish_the_base_contract_response_component()
    {
        var document = ApiContractTestHost.GetSwaggerDocument();

        var referenced = ReferencedByOperationsUnder(document, StockPath);

        Assert.Contains("StockAdjustment", referenced);
        Assert.DoesNotContain("ProductStockAdjustmentResponse", referenced);
    }

    /// <summary>
    /// A pinned response description is only honest while the type that actually serialises is
    /// schema-identical to it. The stock endpoints serialise
    /// <c>ProductStockAdjustmentResponse</c> - which the product endpoints publish under its own id
    /// for a movement in a product's history (issue #303) - against a published
    /// <c>StockAdjustment</c> description, so the two components must stay the same shape: same
    /// properties, same order, same types, same nullability, same enum references. The runtime
    /// payload equality is proved separately by
    /// <c>InventoryApi.Tests.DTOs.StockAdjustmentResponseJsonContractTests</c>.
    /// </summary>
    [Fact]
    public void Pinned_stock_response_describes_the_shape_the_api_owned_dto_serializes()
    {
        var document = ApiContractTestHost.GetSwaggerDocument();

        var pinned = Normalize(
            document.Components.Schemas["StockAdjustment"].SerializeAsJson(OpenApiSpecVersion.OpenApi3_0));
        var apiOwned = Normalize(
            document.Components.Schemas["ProductStockAdjustmentResponse"]
                .SerializeAsJson(OpenApiSpecVersion.OpenApi3_0));

        Assert.Equal(pinned, apiOwned);
    }

    /// <summary>
    /// The legacy <c>StockAdjustment</c> component is reached from two places, both of which the
    /// Swagger compatibility boundary owns: the pinned stock response and the legacy <c>Product</c>
    /// component the pinned purchase/supplier-order schemas reference.
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

        // One vocabulary, reached from the request body, the pinned response, the API-owned response
        // DTO the product endpoints publish, and the legacy entity component.
        Assert.Equal(
            "StockAdjustmentReason",
            document.Components.Schemas["StockAdjustmentDto"].Properties["reason"].Reference?.Id);
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

    /// <summary>
    /// The request-side compatibility exception, proved rather than asserted. The document publishes
    /// one <c>StockAdjustmentReason</c> component, derived from the persistence CLR enum that
    /// <see cref="InventoryApi.DTOs.StockAdjustmentDto.Reason"/> carries and that the pinned
    /// response components reach, so a second CLR enum of the same simple name - an API-owned
    /// <c>InventoryApi.DTOs.StockAdjustmentReason</c>, which is what removing the request DTO's
    /// dependency on <c>InventoryApi.Models</c> would need - cannot be registered beside it. The
    /// stand-in below reproduces the exact failure Swashbuckle raises, through the application's own
    /// schema generator and schema-id selector.
    ///
    /// The first assertion is what dates this exception: once the pinned components stop publishing
    /// the persistence enum (issues #153/#154 relocate the persistence model and retire the legacy
    /// <c>Product</c>/<c>StockAdjustment</c> pins), it fails, and the stock DTOs can become fully
    /// API-owned with no change to the published contract.
    /// </summary>
    [Fact]
    public void Stock_adjustment_reason_cannot_become_api_owned_while_the_published_components_describe_it()
    {
        var document = ApiContractTestHost.GetSwaggerDocument();

        Assert.Contains("StockAdjustmentReason", document.Components.Schemas.Keys);
        Assert.Equal(
            "StockAdjustmentReason",
            document.Components.Schemas["StockAdjustmentDto"].Properties["reason"].Reference?.Id);

        var generator = ApiContractTestHost.GetSchemaGenerator();
        var repository = new SchemaRepository("v1");
        generator.GenerateSchema(typeof(Models.StockAdjustmentReason), repository);

        var collision = Record.Exception(() =>
            generator.GenerateSchema(typeof(ApiOwned.StockAdjustmentReason), repository));

        Assert.NotNull(collision);
        Assert.Contains("Can't use schemaId", Flatten(collision), StringComparison.Ordinal);
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

    private static string Normalize(string json) =>
        JsonNode.Parse(json)!.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    /// <summary>Every message in an exception chain, so the assertion does not depend on which
    /// layer of Swashbuckle wrapped the failure.</summary>
    private static string Flatten(Exception exception) =>
        exception.InnerException is null
            ? exception.Message
            : $"{exception.Message} {Flatten(exception.InnerException)}";

    /// <summary>
    /// Stands in for the <c>InventoryApi.DTOs.StockAdjustmentReason</c> that issue #305 could not
    /// introduce: a second CLR enum whose simple name - and therefore whose derived schema id - is
    /// <c>StockAdjustmentReason</c>. It exists only inside
    /// <see cref="Stock_adjustment_reason_cannot_become_api_owned_while_the_published_components_describe_it"/>,
    /// never in a published document.
    /// </summary>
    private static class ApiOwned
    {
        internal enum StockAdjustmentReason
        {
            Restock,
            Sale,
            Damaged,
            Expired,
            Correction,
            MachineRefill,
        }
    }

    /// <summary>
    /// The base branch's published stock operations, captured from the real document generation with
    /// the declared response types <c>develop</c> had (<c>ActionResult&lt;IEnumerable&lt;StockAdjustment&gt;&gt;</c>
    /// and <c>ActionResult&lt;StockAdjustment&gt;</c>). Keep these literal: they are the
    /// client-visible contract, not a restatement of whatever the current CLR types happen to
    /// produce.
    /// </summary>
    private static readonly Dictionary<string, string> BaseStockOperations = new(StringComparer.Ordinal)
    {
        [$"{OperationType.Get} {StockPath}"] = """
        {
          "tags": [ "Stock" ],
          "parameters": [
            {
              "name": "productId",
              "in": "path",
              "required": true,
              "schema": { "type": "integer", "format": "int64" }
            }
          ],
          "responses": {
            "200": {
              "description": "OK",
              "content": {
                "text/plain": {
                  "schema": {
                    "type": "array",
                    "items": { "$ref": "#/components/schemas/StockAdjustment" }
                  }
                },
                "application/json": {
                  "schema": {
                    "type": "array",
                    "items": { "$ref": "#/components/schemas/StockAdjustment" }
                  }
                },
                "text/json": {
                  "schema": {
                    "type": "array",
                    "items": { "$ref": "#/components/schemas/StockAdjustment" }
                  }
                }
              }
            }
          }
        }
        """,
        [$"{OperationType.Post} {StockPath}"] = """
        {
          "tags": [ "Stock" ],
          "parameters": [
            {
              "name": "productId",
              "in": "path",
              "required": true,
              "schema": { "type": "integer", "format": "int64" }
            }
          ],
          "requestBody": {
            "content": {
              "application/json": { "schema": { "$ref": "#/components/schemas/StockAdjustmentDto" } },
              "text/json": { "schema": { "$ref": "#/components/schemas/StockAdjustmentDto" } },
              "application/*+json": { "schema": { "$ref": "#/components/schemas/StockAdjustmentDto" } }
            }
          },
          "responses": {
            "200": {
              "description": "OK",
              "content": {
                "text/plain": { "schema": { "$ref": "#/components/schemas/StockAdjustment" } },
                "application/json": { "schema": { "$ref": "#/components/schemas/StockAdjustment" } },
                "text/json": { "schema": { "$ref": "#/components/schemas/StockAdjustment" } }
              }
            }
          }
        }
        """,
        [$"{OperationType.Get} {RestockCostSuggestionPath}"] = """
        {
          "tags": [ "Stock" ],
          "parameters": [
            {
              "name": "productId",
              "in": "path",
              "required": true,
              "schema": { "type": "integer", "format": "int64" }
            }
          ],
          "responses": {
            "200": {
              "description": "OK",
              "content": {
                "text/plain": { "schema": { "$ref": "#/components/schemas/RestockCostSuggestionDto" } },
                "application/json": { "schema": { "$ref": "#/components/schemas/RestockCostSuggestionDto" } },
                "text/json": { "schema": { "$ref": "#/components/schemas/RestockCostSuggestionDto" } }
              }
            }
          }
        }
        """,
    };

    /// <summary>
    /// The base branch's published components for the stock endpoints, captured the same way. The
    /// <c>StockAdjustment</c> baseline is the one the pinned response points at; <c>StockAdjustmentDto</c>
    /// is the request body, including the <c>reason</c> reference that keeps the request side on the
    /// persistence enum's published vocabulary.
    /// </summary>
    private static readonly Dictionary<string, string> BaseStockComponents = new(StringComparer.Ordinal)
    {
        ["StockAdjustment"] = """
        {
          "type": "object",
          "properties": {
            "id": { "type": "integer", "format": "int32" },
            "productId": { "type": "integer", "format": "int64" },
            "receiptItemId": { "type": "integer", "format": "int32", "nullable": true },
            "quantityChange": { "type": "integer", "format": "int32" },
            "quantityAfter": { "type": "integer", "format": "int32" },
            "unitCost": { "type": "number", "format": "double", "nullable": true },
            "totalCost": { "type": "number", "format": "double", "nullable": true },
            "costingQuantityAfter": { "type": "integer", "format": "int32", "nullable": true },
            "averageUnitCostAfter": { "type": "number", "format": "double", "nullable": true },
            "inventoryValueAfter": { "type": "number", "format": "double", "nullable": true },
            "reason": { "$ref": "#/components/schemas/StockAdjustmentReason" },
            "source": { "$ref": "#/components/schemas/StockAdjustmentSource" },
            "machineId": { "type": "integer", "format": "int64", "nullable": true },
            "notes": { "type": "string", "nullable": true },
            "eatBefore": { "type": "string", "format": "date-time", "nullable": true },
            "createdAt": { "type": "string", "format": "date-time" },
            "effectiveAt": { "type": "string", "format": "date-time" }
          },
          "additionalProperties": false
        }
        """,
        ["StockAdjustmentDto"] = """
        {
          "type": "object",
          "properties": {
            "quantityChange": { "type": "integer", "format": "int32" },
            "reason": { "$ref": "#/components/schemas/StockAdjustmentReason" },
            "notes": { "type": "string", "nullable": true },
            "machineId": { "type": "integer", "format": "int64", "nullable": true },
            "eatBefore": { "type": "string", "format": "date-time", "nullable": true },
            "unitCost": { "type": "number", "format": "double", "nullable": true }
          },
          "additionalProperties": false
        }
        """,
        ["StockAdjustmentReason"] = """
        { "enum": [ 0, 1, 2, 3, 4, 5 ], "type": "integer", "format": "int32" }
        """,
        ["StockAdjustmentSource"] = """
        { "enum": [ 0, 1 ], "type": "integer", "format": "int32" }
        """,
        ["RestockCostSuggestionDto"] = """
        {
          "type": "object",
          "properties": {
            "unitCost": { "type": "number", "format": "double", "nullable": true },
            "source": { "type": "string", "nullable": true },
            "purchaseDate": { "type": "string", "format": "date-time", "nullable": true }
          },
          "additionalProperties": false
        }
        """,
    };
}
