#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using InventoryApi.Swagger;
using Microsoft.OpenApi.Models;
using Xunit;

namespace InventoryApi.Tests.Swagger;

/// <summary>
/// Locks the generated OpenAPI document across the internal Receipt-to-Purchase rename
/// (issue #60). Swashbuckle names schemas after CLR types and tags after controller names, so
/// without <see cref="LegacyOpenApiCompatibility"/> the rename would republish
/// <c>Receipt</c>/<c>ReceiptItem</c>/<c>ReceiptResponseDto</c>/<c>ReceiptValidationDto</c> and the
/// <c>Receipts</c> tag under new <c>Purchase*</c> names, breaking generated clients even though the
/// routes and JSON keys did not change.
/// </summary>
public class LegacyOpenApiContractTests
{
    private const string PurchasesPath = "/api/receipts";
    private const string PurchaseByIdPath = "/api/receipts/{id}";

    [Theory]
    [InlineData("Receipt")]
    [InlineData("ReceiptItem")]
    [InlineData("ReceiptResponseDto")]
    [InlineData("ReceiptValidationDto")]
    public void Legacy_schema_identifier_is_still_published(string schemaId)
    {
        var document = ApiContractTestHost.GetSwaggerDocument();

        Assert.True(
            document.Components.Schemas.ContainsKey(schemaId),
            $"Schema '{schemaId}' is missing. Published schemas: " +
            string.Join(", ", document.Components.Schemas.Keys.Order()));
    }

    [Fact]
    public void No_schema_was_republished_under_a_new_purchase_identifier()
    {
        var document = ApiContractTestHost.GetSwaggerDocument();

        var renamed = document.Components.Schemas.Keys
            .Where(key => key.StartsWith("Purchase", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(renamed);
    }

    [Fact]
    public void Purchase_operations_keep_the_legacy_receipts_tag()
    {
        var document = ApiContractTestHost.GetSwaggerDocument();

        var operations = PurchaseOperations(document).ToList();

        Assert.NotEmpty(operations);
        Assert.All(operations, operation =>
            Assert.Equal(
                new[] { LegacyOpenApiCompatibility.LegacyPurchasesTag },
                operation.Tags.Select(tag => tag.Name)));
    }

    [Fact]
    public void No_operation_is_published_under_a_purchases_tag()
    {
        var document = ApiContractTestHost.GetSwaggerDocument();

        var tags = document.Paths
            .SelectMany(path => path.Value.Operations.Values)
            .SelectMany(operation => operation.Tags)
            .Select(tag => tag.Name)
            .Distinct()
            .ToList();

        Assert.DoesNotContain("Purchases", tags);
    }

    [Fact]
    public void Purchase_operations_reference_the_legacy_response_schema_identifiers()
    {
        var document = ApiContractTestHost.GetSwaggerDocument();

        var referencedSchemaIds = PurchaseOperations(document)
            .SelectMany(operation => operation.Responses.Values)
            .SelectMany(response => response.Content.Values)
            .Select(media => media.Schema)
            .SelectMany(ReferencedSchemaIds)
            .Distinct()
            .ToList();

        Assert.Contains("ReceiptResponseDto", referencedSchemaIds);
        Assert.DoesNotContain(referencedSchemaIds, id => id.StartsWith("Purchase", StringComparison.Ordinal));
    }

    [Fact]
    public void Legacy_response_schema_still_exposes_the_receipt_and_validation_properties()
    {
        var document = ApiContractTestHost.GetSwaggerDocument();

        var response = document.Components.Schemas["ReceiptResponseDto"];

        Assert.Equal(
            new[] { "receipt", "validation" },
            response.Properties.Keys.Order().ToArray());
        Assert.Equal("Receipt", response.Properties["receipt"].Reference?.Id);
        Assert.Equal("ReceiptValidationDto", response.Properties["validation"].Reference?.Id);
        Assert.Equal("ReceiptItem", document.Components.Schemas["Receipt"].Properties["items"].Items.Reference?.Id);
        Assert.Contains("receiptId", document.Components.Schemas["ReceiptItem"].Properties.Keys);
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
    public void Purchase_by_id_path_is_published_for_the_legacy_route()
    {
        var document = ApiContractTestHost.GetSwaggerDocument();

        Assert.True(document.Paths.ContainsKey(PurchasesPath));
        Assert.True(document.Paths.ContainsKey(PurchaseByIdPath));
        Assert.True(document.Paths.ContainsKey(PurchaseByIdPath + "/file"));
    }
}
