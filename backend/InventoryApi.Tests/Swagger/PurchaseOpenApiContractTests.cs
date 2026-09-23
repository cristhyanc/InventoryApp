#nullable enable
using Microsoft.OpenApi.Models;
using Xunit;

namespace InventoryApi.Tests.Swagger;

/// <summary>
/// Locks the canonical generated OpenAPI document for the Purchase business record (issue
/// #127): Swashbuckle's default schema ids (derived from CLR type names) and tag (derived from
/// the controller name) are published as-is, with no legacy Receipt-named schema/tag surviving.
/// </summary>
public class PurchaseOpenApiContractTests
{
    private const string PurchasesPath = "/api/purchases";
    private const string PurchaseByIdPath = "/api/purchases/{id}";

    [Theory]
    [InlineData("Purchase")]
    [InlineData("PurchaseItem")]
    [InlineData("PurchaseResponseDto")]
    [InlineData("PurchaseValidationDto")]
    public void Canonical_schema_identifier_is_published(string schemaId)
    {
        var document = ApiContractTestHost.GetSwaggerDocument();

        Assert.True(
            document.Components.Schemas.ContainsKey(schemaId),
            $"Schema '{schemaId}' is missing. Published schemas: " +
            string.Join(", ", document.Components.Schemas.Keys.Order()));
    }

    [Fact]
    public void No_schema_is_published_under_a_legacy_receipt_identifier()
    {
        var document = ApiContractTestHost.GetSwaggerDocument();

        var legacy = document.Components.Schemas.Keys
            .Where(key => key.StartsWith("Receipt", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(legacy);
    }

    [Fact]
    public void Purchase_operations_use_the_purchases_tag()
    {
        var document = ApiContractTestHost.GetSwaggerDocument();

        var operations = PurchaseOperations(document).ToList();

        Assert.NotEmpty(operations);
        Assert.All(operations, operation =>
            Assert.Equal(
                new[] { "Purchases" },
                operation.Tags.Select(tag => tag.Name)));
    }

    [Fact]
    public void No_operation_is_published_under_the_legacy_receipts_tag()
    {
        var document = ApiContractTestHost.GetSwaggerDocument();

        var tags = document.Paths
            .SelectMany(path => path.Value.Operations.Values)
            .SelectMany(operation => operation.Tags)
            .Select(tag => tag.Name)
            .Distinct()
            .ToList();

        Assert.DoesNotContain("Receipts", tags);
    }

    [Fact]
    public void Purchase_operations_reference_the_canonical_response_schema_identifiers()
    {
        var document = ApiContractTestHost.GetSwaggerDocument();

        var referencedSchemaIds = PurchaseOperations(document)
            .SelectMany(operation => operation.Responses.Values)
            .SelectMany(response => response.Content.Values)
            .Select(media => media.Schema)
            .SelectMany(ReferencedSchemaIds)
            .Distinct()
            .ToList();

        Assert.Contains("PurchaseResponseDto", referencedSchemaIds);
        Assert.DoesNotContain(referencedSchemaIds, id => id.StartsWith("Receipt", StringComparison.Ordinal));
    }

    [Fact]
    public void Canonical_response_schema_exposes_the_purchase_and_validation_properties()
    {
        var document = ApiContractTestHost.GetSwaggerDocument();

        var response = document.Components.Schemas["PurchaseResponseDto"];

        Assert.Equal(
            new[] { "purchase", "validation" },
            response.Properties.Keys.Order().ToArray());
        Assert.Equal("Purchase", response.Properties["purchase"].Reference?.Id);
        Assert.Equal("PurchaseValidationDto", response.Properties["validation"].Reference?.Id);
        Assert.Equal("PurchaseItem", document.Components.Schemas["Purchase"].Properties["items"].Items.Reference?.Id);

        // `receiptId` is the persistence-facing field issue #127 deliberately leaves unrenamed
        // (the physical Receipt-named schema stays); keep it locked in the published contract.
        Assert.Contains("receiptId", document.Components.Schemas["PurchaseItem"].Properties.Keys);
    }

    private static IEnumerable<OpenApiOperation> PurchaseOperations(OpenApiDocument document) =>
        document.Paths
            .Where(path => path.Key.StartsWith(PurchasesPath, StringComparison.Ordinal))
            .SelectMany(path => path.Value.Operations.Values);

    /// <summary>Schema ids a response schema points at, directly or through an array item.</summary>
    private static IEnumerable<string> ReferencedSchemaIds(OpenApiSchema? schema)
    {
        if (schema is null) yield break;
        if (schema.Reference?.Id is { } id) yield return id;
        if (schema.Items?.Reference?.Id is { } itemId) yield return itemId;
    }

    [Fact]
    public void Purchase_by_id_path_is_published_for_the_canonical_route()
    {
        var document = ApiContractTestHost.GetSwaggerDocument();

        Assert.True(document.Paths.ContainsKey(PurchasesPath));
        Assert.True(document.Paths.ContainsKey(PurchaseByIdPath));
        Assert.True(document.Paths.ContainsKey(PurchaseByIdPath + "/file"));
    }
}
