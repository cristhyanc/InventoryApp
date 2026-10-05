#nullable enable
using Microsoft.OpenApi.Models;
using Xunit;

namespace InventoryApi.Tests.Swagger;

/// <summary>
/// The published OpenAPI description of the global stock-history query (issue #384).
///
/// This endpoint is new, so there is no base-branch document to compare it with the way
/// <see cref="StockAndExpenseSchemaContractTests"/> compares the endpoints issue #305 touched.
/// What is locked here instead is the contract this issue publishes, property by property and
/// parameter by parameter: the response is the API-owned <c>StockHistoryPageResponse</c>/
/// <c>StockHistoryEntryResponse</c> pair and never an EF entity, the bounded-page envelope is part
/// of the contract rather than an implementation detail, and the reason/source vocabulary is the
/// one component the document already publishes rather than a second copy under a new name.
///
/// The endpoints issue #305 pinned are untouched by this one: their operations and components stay
/// compared literally against the base branch in <see cref="StockAndExpenseSchemaContractTests"/>,
/// which fails if this change moved them.
/// </summary>
public class StockHistoryOpenApiContractTests
{
    private const string StockHistoryPath = "/api/stock-history";

    private static OpenApiOperation Operation() =>
        ApiContractTestHost.GetSwaggerDocument().Paths[StockHistoryPath].Operations[OperationType.Get];

    [Fact]
    public void The_global_stock_history_operation_publishes_the_api_owned_page_response()
    {
        var operation = Operation();

        var response = operation.Responses["200"];
        Assert.Contains("application/json", response.Content.Keys);
        foreach (var mediaType in response.Content.Values)
        {
            Assert.Equal("StockHistoryPageResponse", mediaType.Schema.Reference?.Id);
        }
    }

    /// <summary>
    /// The query parameters are the filter surface a client codes against. Every one of them is
    /// optional - an unfiltered request is the all-products view - and the date range is a pair of
    /// calendar days the server interprets in <c>Australia/Sydney</c>.
    /// </summary>
    [Fact]
    public void The_global_stock_history_operation_publishes_exactly_its_filter_parameters()
    {
        var operation = Operation();

        Assert.Equal(
            ["productId", "from", "to", "reason", "machineId", "source", "page", "pageSize"],
            operation.Parameters.Select(parameter => parameter.Name).ToArray());
        Assert.All(operation.Parameters, parameter => Assert.Equal(ParameterLocation.Query, parameter.In));
        Assert.All(operation.Parameters, parameter => Assert.False(parameter.Required));

        var parameters = operation.Parameters.ToDictionary(parameter => parameter.Name, StringComparer.Ordinal);
        AssertScalar(parameters["productId"].Schema, "integer", "int64");
        AssertScalar(parameters["from"].Schema, "string", "date-time");
        AssertScalar(parameters["to"].Schema, "string", "date-time");
        AssertScalar(parameters["machineId"].Schema, "integer", "int64");
        AssertScalar(parameters["page"].Schema, "integer", "int32");
        AssertScalar(parameters["pageSize"].Schema, "integer", "int32");
        Assert.Equal("StockAdjustmentReason", parameters["reason"].Schema.Reference?.Id);
        Assert.Equal("StockAdjustmentSource", parameters["source"].Schema.Reference?.Id);
    }

    /// <summary>
    /// The bounded-result envelope: the page the server actually served (not the one that was
    /// asked for), the complete count behind it, and whether more remains.
    /// </summary>
    [Fact]
    public void The_page_response_publishes_the_bounded_result_envelope()
    {
        var schema = ApiContractTestHost.GetSwaggerDocument().Components.Schemas["StockHistoryPageResponse"];

        Assert.Equal(["items", "page", "pageSize", "totalCount", "hasMore"], schema.Properties.Keys.ToArray());
        Assert.Equal("array", schema.Properties["items"].Type);
        Assert.Equal("StockHistoryEntryResponse", schema.Properties["items"].Items.Reference?.Id);
        AssertScalar(schema.Properties["page"], "integer", "int32");
        AssertScalar(schema.Properties["pageSize"], "integer", "int32");
        AssertScalar(schema.Properties["totalCount"], "integer", "int32");
        Assert.Equal("boolean", schema.Properties["hasMore"].Type);
    }

    /// <summary>
    /// One movement as the page reads it: every persisted field the table needs, plus the product
    /// name, so the client does not have to fan out a request per product to label a row.
    /// <c>effectiveAt</c> is deliberately absent - this query orders and filters on
    /// <c>createdAt</c>, and the costing effective instant is not part of this contract.
    /// </summary>
    [Fact]
    public void The_entry_response_publishes_the_movement_fields_the_page_displays()
    {
        var schema = ApiContractTestHost.GetSwaggerDocument().Components.Schemas["StockHistoryEntryResponse"];

        Assert.Equal(
            [
                "id", "productId", "productName", "receiptItemId", "quantityChange", "quantityAfter",
                "unitCost", "totalCost", "costingQuantityAfter", "averageUnitCostAfter", "inventoryValueAfter",
                "reason", "source", "machineId", "notes", "eatBefore", "createdAt",
            ],
            schema.Properties.Keys.ToArray());

        AssertScalar(schema.Properties["id"], "integer", "int32");
        AssertScalar(schema.Properties["productId"], "integer", "int64");
        Assert.Equal("string", schema.Properties["productName"].Type);
        AssertScalar(schema.Properties["receiptItemId"], "integer", "int32");
        AssertScalar(schema.Properties["quantityChange"], "integer", "int32");
        AssertScalar(schema.Properties["quantityAfter"], "integer", "int32");
        AssertScalar(schema.Properties["unitCost"], "number", "double");
        AssertScalar(schema.Properties["totalCost"], "number", "double");
        AssertScalar(schema.Properties["costingQuantityAfter"], "integer", "int32");
        AssertScalar(schema.Properties["averageUnitCostAfter"], "number", "double");
        AssertScalar(schema.Properties["inventoryValueAfter"], "number", "double");
        AssertScalar(schema.Properties["machineId"], "integer", "int64");
        Assert.Equal("string", schema.Properties["notes"].Type);
        AssertScalar(schema.Properties["eatBefore"], "string", "date-time");
        AssertScalar(schema.Properties["createdAt"], "string", "date-time");

        // The one published stock-adjustment vocabulary, not a second copy: see
        // StockAndExpenseSchemaContractTests for why these stay the InventoryApi.Models enums.
        Assert.Equal("StockAdjustmentReason", schema.Properties["reason"].Reference?.Id);
        Assert.Equal("StockAdjustmentSource", schema.Properties["source"].Reference?.Id);
    }

    [Fact]
    public void The_global_stock_history_operation_publishes_no_ef_entity()
    {
        var document = ApiContractTestHost.GetSwaggerDocument();

        var referenced = document.Paths[StockHistoryPath]
            .Operations.Values
            .SelectMany(operation => operation.Responses.Values)
            .SelectMany(response => response.Content.Values)
            .Select(media => media.Schema)
            .SelectMany(schema => new[] { schema.Reference?.Id, schema.Items?.Reference?.Id })
            .Where(id => id is not null)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["StockHistoryPageResponse"], referenced);
        Assert.DoesNotContain("StockAdjustment", referenced);

        // The entry rows are reached through the page envelope, and that reference is the API-owned
        // DTO too - the persistence entity's component is never the item type of this response.
        Assert.Equal(
            "StockHistoryEntryResponse",
            document.Components.Schemas["StockHistoryPageResponse"].Properties["items"].Items.Reference?.Id);
    }

    private static void AssertScalar(OpenApiSchema schema, string type, string format)
    {
        Assert.Equal(type, schema.Type);
        Assert.Equal(format, schema.Format);
    }
}
