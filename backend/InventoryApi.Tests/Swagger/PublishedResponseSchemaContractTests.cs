#nullable enable
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Extensions;
using Microsoft.OpenApi.Models;
using Xunit;

namespace InventoryApi.Tests.Swagger;

/// <summary>
/// Locks the published OpenAPI schema of the purchase and supplier-order response bodies against
/// the contract this pull request's base branch generated.
///
/// Issue #304 replaced the serialised EF <c>Purchase</c>/<c>PurchaseItem</c>/<c>SupplierOrder</c>/
/// <c>SupplierOrderLine</c> entities with API-owned response DTOs and excludes API-contract
/// changes, so the generated document - not only the runtime JSON - has to come out unchanged.
/// Byte-identical JSON does not prove that: Swashbuckle derives schema ids from CLR type names and
/// a <c>required</c> list from C# <c>required</c> members, so swapping the CLR type behind a
/// response silently renames its schema and can add requiredness no client was told about.
/// <c>InventoryApi.Swagger.PublishedResponseSchemaContract</c> holds both still, and these tests
/// are the regression coverage for it.
///
/// The baselines in <see cref="BaseContractSchemas"/> are the generated schemas of the base branch
/// (<c>develop</c>), captured by running the real document generation over the EF entity types the
/// endpoints serialised there. The comparison is whole-schema, so a change to any property name,
/// order, type, format, nullability, <c>readOnly</c> flag, requiredness or
/// <c>additionalProperties</c> fails here.
/// </summary>
public class PublishedResponseSchemaContractTests
{
    private const string SupplierOrdersPath = "/api/SupplierOrders";

    [Theory]
    [InlineData("Purchase")]
    [InlineData("PurchaseItem")]
    [InlineData("SupplierOrder")]
    [InlineData("SupplierOrderLine")]
    public void Published_schema_identifier_survives_the_api_owned_response_types(string schemaId)
    {
        var document = ApiContractTestHost.GetSwaggerDocument();

        Assert.True(
            document.Components.Schemas.ContainsKey(schemaId),
            $"Schema '{schemaId}' is missing. Published schemas: " +
            string.Join(", ", document.Components.Schemas.Keys.Order()));
    }

    /// <summary>
    /// The CLR names of the API-owned response DTOs. None of them may reach the published document:
    /// they are internal names, and publishing them would rename four client-visible schemas.
    /// </summary>
    [Theory]
    [InlineData("PurchaseResponse")]
    [InlineData("PurchaseItemResponse")]
    [InlineData("SupplierOrderResponse")]
    [InlineData("SupplierOrderLineResponse")]
    public void Internal_response_dto_name_is_not_published(string clrName)
    {
        var document = ApiContractTestHost.GetSwaggerDocument();

        Assert.DoesNotContain(clrName, document.Components.Schemas.Keys);
    }

    [Theory]
    [InlineData("Purchase")]
    [InlineData("PurchaseItem")]
    [InlineData("SupplierOrder")]
    [InlineData("SupplierOrderLine")]
    public void Published_schema_matches_the_base_contract(string schemaId)
    {
        var document = ApiContractTestHost.GetSwaggerDocument();

        var published = Normalize(document.Components.Schemas[schemaId].SerializeAsJson(OpenApiSpecVersion.OpenApi3_0));

        Assert.Equal(Normalize(ExpectedSchema(schemaId)), published);
    }

    /// <summary>
    /// The response DTOs declare C# <c>required</c> members so a response mapper cannot forget a
    /// field, but the document never described these schemas as having required properties and a
    /// generated client would start validating responses more strictly if it did.
    /// </summary>
    [Theory]
    [InlineData("Purchase")]
    [InlineData("PurchaseItem")]
    [InlineData("SupplierOrder")]
    [InlineData("SupplierOrderLine")]
    public void Published_schema_declares_no_required_properties(string schemaId)
    {
        var document = ApiContractTestHost.GetSwaggerDocument();

        Assert.Empty(document.Components.Schemas[schemaId].Required);
    }

    /// <summary>
    /// A pinned id is only safe while nothing else publishes the EF entity it belonged to. If one
    /// ever does, Swashbuckle fails document generation with a duplicate-schema-id error, which
    /// this test turns into a named failure rather than a generic one elsewhere in the suite.
    /// </summary>
    [Fact]
    public void Document_generates_without_a_schema_identifier_collision()
    {
        var document = ApiContractTestHost.GetSwaggerDocument();

        Assert.NotEmpty(document.Components.Schemas);
        Assert.Equal(
            document.Components.Schemas.Keys.Count,
            document.Components.Schemas.Keys.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// The purchase/supplier-order persistence model is full of deliberately unrenamed
    /// Receipt-named surfaces (the `Receipts` table, `SupplierOrderReceiptAllocation`). None of them
    /// may reach the published document - the rule issue #127 established and issue #304 keeps.
    /// </summary>
    [Fact]
    public void No_schema_or_tag_carries_a_receipt_name()
    {
        var document = ApiContractTestHost.GetSwaggerDocument();

        Assert.Empty(document.Components.Schemas.Keys
            .Where(key => key.Contains("Receipt", StringComparison.Ordinal)));
        Assert.Empty(document.Paths
            .SelectMany(path => path.Value.Operations.Values)
            .SelectMany(operation => operation.Tags)
            .Select(tag => tag.Name)
            .Where(name => name is not null && name.Contains("Receipt", StringComparison.Ordinal)));
    }

    [Fact]
    public void Supplier_order_operations_reference_the_published_response_schema()
    {
        var document = ApiContractTestHost.GetSwaggerDocument();

        var referenced = document.Paths
            .Where(path => path.Key.StartsWith(SupplierOrdersPath, StringComparison.Ordinal))
            .SelectMany(path => path.Value.Operations.Values)
            .SelectMany(operation => operation.Responses.Values)
            .SelectMany(response => response.Content.Values)
            .Select(media => media.Schema)
            .SelectMany(ReferencedSchemaIds)
            .Distinct()
            .ToList();

        Assert.Contains("SupplierOrder", referenced);
        Assert.DoesNotContain("SupplierOrderResponse", referenced);
    }

    [Fact]
    public void Supplier_order_status_is_published_once_with_the_same_numeric_values()
    {
        var document = ApiContractTestHost.GetSwaggerDocument();

        // The status moved from InventoryApi.Models.SupplierOrderStatus to the Domain enum, which
        // mirrors it member for member; both derive the same schema id, so a document carrying two
        // of them would not have generated at all.
        var status = document.Components.Schemas["SupplierOrderStatus"];

        Assert.Equal("integer", status.Type);
        Assert.Equal("int32", status.Format);
        Assert.Equal(
            new[] { 0, 1, 2, 3 },
            status.Enum.OfType<Microsoft.OpenApi.Any.OpenApiInteger>().Select(value => value.Value).ToArray());
        Assert.Equal(
            "SupplierOrderStatus",
            document.Components.Schemas["SupplierOrder"].Properties["status"].Reference?.Id);
        Assert.Equal(
            "SupplierOrderLine",
            document.Components.Schemas["SupplierOrder"].Properties["lines"].Items.Reference?.Id);
    }

    /// <summary>Schema ids a schema points at, directly or through an array item.</summary>
    private static IEnumerable<string> ReferencedSchemaIds(OpenApiSchema? schema)
    {
        if (schema is null) yield break;
        if (schema.Reference?.Id is { } id) yield return id;
        if (schema.Items?.Reference?.Id is { } itemId) yield return itemId;
    }

    private static string Normalize(string json) =>
        JsonNode.Parse(json)!.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    /// <summary>
    /// The base branch's schema for <paramref name="schemaId"/>, with the one difference this slice
    /// could not avoid applied to it: the nested supplier and product objects are now the API-owned
    /// <c>SupplierResponse</c>/<c>ProductResponse</c> the supplier and product endpoints already
    /// publish (issues #302/#303), because issue #304 forbids the response mappers from touching
    /// <c>InventoryApi.Models</c> and those two ids are already taken by the EF entities other
    /// endpoints still serialise. Nothing else about these schemas may differ, which is what makes
    /// this substitution list the complete, reviewable record of the deviation.
    /// </summary>
    private static string ExpectedSchema(string schemaId)
    {
        var expected = BaseContractSchemas[schemaId];
        foreach (var (entityRef, apiOwnedRef) in NestedResponseTypeRenames)
        {
            expected = expected.Replace(entityRef, apiOwnedRef, StringComparison.Ordinal);
        }

        return expected;
    }

    private static readonly (string EntityRef, string ApiOwnedRef)[] NestedResponseTypeRenames =
    [
        ("\"#/components/schemas/Supplier\"", "\"#/components/schemas/SupplierResponse\""),
        ("\"#/components/schemas/Product\"", "\"#/components/schemas/ProductResponse\""),
    ];

    /// <summary>
    /// The generated schemas of this pull request's base branch, captured from the real document
    /// generation. Keep these literal: they are the client-visible contract, not a restatement of
    /// whatever the current CLR types happen to produce.
    /// </summary>
    private static readonly Dictionary<string, string> BaseContractSchemas = new(StringComparer.Ordinal)
    {
        ["Purchase"] = """
        {
          "type": "object",
          "properties": {
            "id": { "type": "integer", "format": "int32" },
            "title": { "type": "string", "nullable": true },
            "notes": { "type": "string", "nullable": true },
            "totalAmount": { "type": "number", "format": "double", "nullable": true },
            "deliveryCost": { "type": "number", "format": "double", "nullable": true },
            "packageCost": { "type": "number", "format": "double", "nullable": true },
            "purchaseDate": { "type": "string", "format": "date-time" },
            "supplierId": { "type": "integer", "format": "int32", "nullable": true },
            "supplier": { "$ref": "#/components/schemas/Supplier" },
            "items": {
              "type": "array",
              "items": { "$ref": "#/components/schemas/PurchaseItem" },
              "nullable": true
            },
            "fileName": { "type": "string", "nullable": true },
            "storedFileName": { "type": "string", "nullable": true },
            "contentType": { "type": "string", "nullable": true },
            "fileSizeBytes": { "type": "integer", "format": "int64" },
            "createdAt": { "type": "string", "format": "date-time" }
          },
          "additionalProperties": false
        }
        """,
        ["PurchaseItem"] = """
        {
          "type": "object",
          "properties": {
            "id": { "type": "integer", "format": "int32" },
            "receiptId": { "type": "integer", "format": "int32" },
            "productId": { "type": "integer", "format": "int64" },
            "product": { "$ref": "#/components/schemas/Product" },
            "quantity": { "type": "number", "format": "double" },
            "unitCost": { "type": "number", "format": "double" },
            "lineTotal": { "type": "number", "format": "double", "readOnly": true }
          },
          "additionalProperties": false
        }
        """,
        ["SupplierOrder"] = """
        {
          "type": "object",
          "properties": {
            "id": { "type": "integer", "format": "int32" },
            "supplierId": { "type": "integer", "format": "int32", "nullable": true },
            "supplier": { "$ref": "#/components/schemas/Supplier" },
            "orderDate": { "type": "string", "format": "date-time" },
            "expectedDate": { "type": "string", "format": "date-time", "nullable": true },
            "reference": { "type": "string", "nullable": true },
            "notes": { "type": "string", "nullable": true },
            "status": { "$ref": "#/components/schemas/SupplierOrderStatus" },
            "createdAt": { "type": "string", "format": "date-time" },
            "updatedAt": { "type": "string", "format": "date-time" },
            "lines": {
              "type": "array",
              "items": { "$ref": "#/components/schemas/SupplierOrderLine" },
              "nullable": true
            }
          },
          "additionalProperties": false
        }
        """,
        ["SupplierOrderLine"] = """
        {
          "type": "object",
          "properties": {
            "id": { "type": "integer", "format": "int32" },
            "supplierOrderId": { "type": "integer", "format": "int32" },
            "productId": { "type": "integer", "format": "int64" },
            "product": { "$ref": "#/components/schemas/Product" },
            "quantityOrdered": { "type": "number", "format": "double" },
            "quantityReceived": { "type": "number", "format": "double" },
            "unitPrice": { "type": "number", "format": "double", "nullable": true },
            "notes": { "type": "string", "nullable": true },
            "outstandingQuantity": { "type": "number", "format": "double", "readOnly": true }
          },
          "additionalProperties": false
        }
        """,
    };
}
