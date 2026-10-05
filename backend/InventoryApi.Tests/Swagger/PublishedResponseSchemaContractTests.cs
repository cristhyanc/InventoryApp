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
/// Byte-identical JSON does not prove that: Swashbuckle derives schema ids from CLR type names, a
/// <c>required</c> list from C# <c>required</c> members, and a nested object's reference from that
/// member's CLR type, so swapping the CLR type behind a response silently renames its schema, can
/// add requiredness no client was told about, and repoints its nested objects at other components.
/// <c>InventoryApi.Swagger.PublishedResponseSchemaContract</c> holds all three still, and these
/// tests are the regression coverage for it.
///
/// The baselines in <see cref="BaseContractSchemas"/> are the generated schemas of the base branch
/// (<c>develop</c>), captured by running the real document generation over the EF entity types the
/// endpoints serialised there, and they are compared literally - no substitution is applied to
/// excuse a difference. The comparison is whole-schema, so a change to any property name, order,
/// type, format, nullability, <c>readOnly</c> flag, requiredness, <c>$ref</c> target or
/// <c>additionalProperties</c> fails here.
/// </summary>
public class PublishedResponseSchemaContractTests
{
    private const string SupplierOrdersPath = "/api/SupplierOrders";
    private const string MachinesPath = "/api/Machines";

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

        Assert.Equal(Normalize(BaseContractSchemas[schemaId]), published);
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
    /// The nested object references, named on their own rather than only inside the whole-schema
    /// comparison, because they are the part a response-type swap changes most quietly: the
    /// mappers build <c>ProductResponse</c>/<c>SupplierResponse</c>, and without the compatibility
    /// boundary Swashbuckle would publish those ids here instead of the ones clients read.
    /// </summary>
    [Theory]
    [InlineData("Purchase", "supplier", "Supplier")]
    [InlineData("PurchaseItem", "product", "Product")]
    [InlineData("SupplierOrder", "supplier", "Supplier")]
    [InlineData("SupplierOrderLine", "product", "Product")]
    public void Nested_object_references_the_published_component(string schemaId, string propertyName, string referencedId)
    {
        var document = ApiContractTestHost.GetSwaggerDocument();

        Assert.Equal(
            referencedId,
            document.Components.Schemas[schemaId].Properties[propertyName].Reference?.Id);
    }

    /// <summary>
    /// A preserved reference is worth nothing if it dangles, so the components it points at have to
    /// be registered with their full base shape. <c>Supplier</c> is compared whole; <c>Product</c>
    /// is checked property by property and through its own nested references, which is what
    /// distinguishes the legacy entity component from the API-owned <c>ProductResponse</c>.
    /// </summary>
    [Fact]
    public void Nested_supplier_component_matches_the_base_contract()
    {
        var document = ApiContractTestHost.GetSwaggerDocument();

        var published = Normalize(document.Components.Schemas["Supplier"].SerializeAsJson(OpenApiSpecVersion.OpenApi3_0));

        Assert.Equal(Normalize(BaseSupplierComponent), published);
    }

    [Fact]
    public void Nested_product_component_keeps_the_base_contract_shape()
    {
        var document = ApiContractTestHost.GetSwaggerDocument();

        var product = document.Components.Schemas["Product"];

        Assert.Equal(BaseProductPropertyNames, product.Properties.Keys.ToArray());
        Assert.Empty(product.Required);
        Assert.Equal("Category", product.Properties["category"].Reference?.Id);
        Assert.Equal("Supplier", product.Properties["supplier"].Reference?.Id);
        Assert.Equal("StockAdjustment", product.Properties["stockAdjustments"].Items.Reference?.Id);
    }

    /// <summary>
    /// Every component a published schema's property points at, directly or as an array item,
    /// resolves to a published component. The compatibility boundary registers the components it
    /// references through Swashbuckle's own generator rather than rewriting reference strings, and
    /// this is what proves the difference between the two.
    /// </summary>
    [Fact]
    public void Every_referenced_schema_is_published()
    {
        var document = ApiContractTestHost.GetSwaggerDocument();

        var published = document.Components.Schemas.Keys.ToHashSet(StringComparer.Ordinal);
        var missing = document.Components.Schemas.Values
            .SelectMany(schema => schema.Properties.Values)
            .SelectMany(ReferencedSchemaIds)
            .Distinct(StringComparer.Ordinal)
            .Where(id => !published.Contains(id))
            .ToArray();

        Assert.Empty(missing);
    }

    /// <summary>
    /// The product and supplier endpoints published <c>ProductResponse</c>/<c>SupplierResponse</c>
    /// before this slice (issues #302/#303) and still do. The compatibility boundary is scoped to
    /// the four purchase/supplier-order schemas and must leave those two contracts alone: no
    /// rename, no cleared requiredness, no repointed nested reference.
    ///
    /// Issue #302 made <c>ProductResponse</c> carry a machine slot's price/commission/MDB/capacity
    /// facts as well, so the machine-product endpoint could stop serialising the EF entity. The
    /// values became settable, the published description must not have: Swashbuckle marks a
    /// property with no setter <c>readOnly</c>, so turning one of those six machine-slot fields
    /// into an <c>init</c> member would have dropped a <c>readOnly</c> flag the document carries
    /// for the product endpoints. <see cref="Published_product_response_read_only_flags_are_unchanged"/>
    /// is the regression guard for that.
    /// </summary>
    [Fact]
    public void Api_owned_product_and_supplier_contracts_are_untouched()
    {
        var document = ApiContractTestHost.GetSwaggerDocument();

        var supplierResponse = Normalize(
            document.Components.Schemas["SupplierResponse"].SerializeAsJson(OpenApiSpecVersion.OpenApi3_0));
        Assert.Equal(Normalize(BaseSupplierResponseComponent), supplierResponse);

        var productResponse = document.Components.Schemas["ProductResponse"];
        Assert.Equal(BaseProductPropertyNames, productResponse.Properties.Keys.ToArray());
        Assert.Equal(
            new[]
            {
                "averageUnitCost", "createdAt", "id", "isActive", "lowStockThreshold",
                "name", "quantityInStock", "restockTo", "unitPrice", "updatedAt",
            },
            productResponse.Required.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("CategoryResponse", productResponse.Properties["category"].Reference?.Id);
        Assert.Equal("SupplierResponse", productResponse.Properties["supplier"].Reference?.Id);
        Assert.Equal(
            "ProductStockAdjustmentResponse",
            productResponse.Properties["stockAdjustments"].Items.Reference?.Id);
    }

    /// <summary>
    /// The exact set of <c>ProductResponse</c> properties the document describes as <c>readOnly</c>,
    /// captured from this pull request's base branch. Twelve of the thirty-four are derived or
    /// response-only on the API side, and a client generated from the document is told so; which
    /// ones they are is contract, not an implementation detail of how the DTO happens to store a
    /// value. Issue #302 added the machine-slot overlay behind the first six of them for the
    /// machine-product response, and it stays behind them precisely so this list does not move.
    /// </summary>
    [Fact]
    public void Published_product_response_read_only_flags_are_unchanged()
    {
        var document = ApiContractTestHost.GetSwaggerDocument();

        var readOnly = document.Components.Schemas["ProductResponse"].Properties
            .Where(property => property.Value.ReadOnly)
            .Select(property => property.Key)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "machinePrice", "commissionValue", "suggestedNetValue", "suggestedPriceValue",
                "mdbCode", "maxStockInMachine", "projectedStockForReorder", "needToOrder",
                "lastEatBefore1", "lastEatBefore2", "isLowStock", "isReorderAlert",
            },
            readOnly);
    }

    /// <summary>
    /// The machine endpoints' published response schemas after issue #302 pointed them at the
    /// API-owned DTOs. This is the one place the generated document changes: the dashboard
    /// operations describe <c>MachineResponse</c> where they described the legacy
    /// <c>Inventory.Infrastructure.Models.Machine</c> type, and the machine-product operation describes the same
    /// <c>ProductResponse</c> the catalogue endpoints have described since issue #303, where it
    /// described the legacy <c>Product</c> entity - the same schema-id derivation issue #303 settled
    /// for a migrated endpoint's own response DTO. The runtime JSON of all three is byte-identical
    /// (<c>InventoryApi.Tests.DTOs.MachineJsonContractTests</c>).
    ///
    /// The legacy <c>Product</c> component itself stays published, because the pinned
    /// purchase/supplier-order schemas still reference it; no machine operation points at it any
    /// more.
    /// </summary>
    [Fact]
    public void Machine_operations_publish_the_api_owned_response_schemas()
    {
        var document = ApiContractTestHost.GetSwaggerDocument();

        var referenced = document.Paths
            .Where(path => path.Key.StartsWith(MachinesPath, StringComparison.OrdinalIgnoreCase))
            .SelectMany(path => path.Value.Operations.Values)
            .SelectMany(operation => operation.Responses.Values)
            .SelectMany(response => response.Content.Values)
            .Select(media => media.Schema)
            .SelectMany(schema => ReferencedSchemaIds(schema).Concat(ReferencedSchemaIds(schema?.Items)))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.Contains("MachineResponse", referenced);
        Assert.Contains("ProductResponse", referenced);
        Assert.DoesNotContain("Machine", referenced);
        Assert.DoesNotContain("Product", referenced);
        Assert.DoesNotContain("Machine", document.Components.Schemas.Keys);
        Assert.Contains("Product", document.Components.Schemas.Keys);
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

        // The status moved from Inventory.Infrastructure.Models.SupplierOrderStatus to the Domain enum, which
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

    /// <summary>
    /// The published <c>Supplier</c> component, the legacy entity schema a purchase's and a
    /// supplier order's <c>supplier</c> has always referenced.
    /// </summary>
    private const string BaseSupplierComponent = """
    {
      "type": "object",
      "properties": {
        "id": { "type": "integer", "format": "int32" },
        "name": { "type": "string", "nullable": true },
        "contactName": { "type": "string", "nullable": true },
        "phone": { "type": "string", "nullable": true },
        "email": { "type": "string", "nullable": true },
        "address": { "type": "string", "nullable": true }
      },
      "additionalProperties": false
    }
    """;

    /// <summary>
    /// The API-owned <c>SupplierResponse</c> the supplier endpoints publish (issue #302). It is
    /// schema-identical to <c>Supplier</c>, which is exactly why nothing but a reference-level
    /// comparison would have caught the nested reference moving between them.
    /// </summary>
    private const string BaseSupplierResponseComponent = BaseSupplierComponent;

    /// <summary>
    /// The property names, in document order, that both the legacy <c>Product</c> component and the
    /// API-owned <c>ProductResponse</c> publish. The two schemas share this list and differ in
    /// requiredness, <c>readOnly</c> flags and their own nested references - the difference the
    /// nested purchase/supplier-order <c>product</c> reference would otherwise have handed clients.
    /// </summary>
    private static readonly string[] BaseProductPropertyNames =
    [
        "id", "name", "sku", "description", "unitPrice", "averageUnitCost", "costingQuantity",
        "inventoryValue", "machinePrice", "commissionValue", "suggestedNetValue",
        "suggestedPriceValue", "mdbCode", "maxStockInMachine", "machineReplenishmentNeed",
        "onOrderQuantity", "projectedStockForReorder", "quantityInStock", "lowStockThreshold",
        "restockTo", "needToOrder", "unit", "isActive", "lastEatBefore1", "lastEatBefore2",
        "createdAt", "updatedAt", "categoryId", "category", "supplierId", "supplier",
        "stockAdjustments", "isLowStock", "isReorderAlert",
    ];

    private static string Normalize(string json) =>
        JsonNode.Parse(json)!.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

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
