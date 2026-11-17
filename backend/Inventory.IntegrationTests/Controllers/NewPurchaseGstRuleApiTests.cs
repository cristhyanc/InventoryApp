using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Inventory.Domain.Gst;
using InventoryApi.Auth.E2ETesting;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// A new purchase's components are classified from the configured rules when the request leaves
/// them out (issue #1348), over the real multipart pipeline and relational SQLite: the product's
/// rule, then the supplier's product-line default for a line; the supplier's fee default for a
/// charge. A submitted classification - including an explicit "not classified" - is still the
/// person's, and an edit never applies a rule.
///
/// It has its own host (a separate fixture instance), because it changes the shared synthetic
/// product's rule and supplier's defaults, which every other purchase test in the suite assumes are
/// unconfigured.
/// </summary>
public sealed class NewPurchaseGstRuleApiTests : IClassFixture<PurchaseGstClassificationApiTests.ApiFactory>
{
    private const string PurchasesUri = "/api/purchases";

    private readonly PurchaseGstClassificationApiTests.ApiFactory _factory;

    public NewPurchaseGstRuleApiTests(PurchaseGstClassificationApiTests.ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task An_omitted_line_classification_takes_the_product_rule()
    {
        var productId = await _factory.ProductIdAsync(E2ETestFixture.PurchaseProductName);
        await SetProductRuleAsync(productId, GstClassification.Taxable);
        try
        {
            var response = await _factory.BusinessA().PostAsync(PurchasesUri, Form(Items(productId, null)));

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var line = await SingleLineAsync(response);
            Assert.Equal((int)GstClassification.Taxable, line.GetProperty("gstClassification").GetInt32());
            Assert.Equal((int)GstClassificationSource.ProductRule, line.GetProperty("gstClassificationSource").GetInt32());
            // 2 x 1.10 = 2.20 GST-inclusive, so 0.20 input GST and nothing left unresolved.
            var gst = await EnvelopeMemberAsync(response, "gst");
            Assert.Equal(0.20m, gst.GetProperty("inputGst").GetDecimal());
            Assert.Equal(0, gst.GetProperty("unresolvedComponentCount").GetInt32());
        }
        finally
        {
            await SetProductRuleAsync(productId, GstRules.None);
        }
    }

    [Theory]
    [InlineData("2", GstClassification.GstFree, GstClassificationSource.Manual)]
    [InlineData("0", GstClassification.Unknown, GstClassificationSource.Unknown)]
    public async Task A_submitted_line_classification_is_kept_over_the_product_rule(
        string submitted, GstClassification expected, GstClassificationSource expectedSource)
    {
        var productId = await _factory.ProductIdAsync(E2ETestFixture.PurchaseProductName);
        await SetProductRuleAsync(productId, GstClassification.Taxable);
        try
        {
            var response = await _factory.BusinessA().PostAsync(PurchasesUri, Form(Items(productId, submitted)));

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var line = await SingleLineAsync(response);
            Assert.Equal((int)expected, line.GetProperty("gstClassification").GetInt32());
            Assert.Equal((int)expectedSource, line.GetProperty("gstClassificationSource").GetInt32());
        }
        finally
        {
            await SetProductRuleAsync(productId, GstRules.None);
        }
    }

    [Fact]
    public async Task Without_a_product_rule_the_supplier_defaults_classify_the_line_and_the_charges()
    {
        var productId = await _factory.ProductIdAsync(E2ETestFixture.PurchaseProductName);
        var supplierId = await SupplierIdAsync(E2ETestFixture.SupplierAName);
        await SetSupplierDefaultsAsync(supplierId, productLines: 2, delivery: 1, package: 0);
        try
        {
            var response = await _factory.BusinessA().PostAsync(PurchasesUri, Form(
                Items(productId, null),
                supplierId: supplierId.ToString(),
                deliveryCost: "5.00",
                packageCost: "2.00"));

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var purchase = await EnvelopeMemberAsync(response, "purchase");
            var line = purchase.GetProperty("items").EnumerateArray().Single();
            Assert.Equal((int)GstClassification.GstFree, line.GetProperty("gstClassification").GetInt32());
            Assert.Equal((int)GstClassificationSource.SupplierDefault, line.GetProperty("gstClassificationSource").GetInt32());
            Assert.Equal((int)GstClassification.Taxable, purchase.GetProperty("deliveryGstClassification").GetInt32());
            Assert.Equal(
                (int)GstClassificationSource.SupplierFeeDefault,
                purchase.GetProperty("deliveryGstClassificationSource").GetInt32());
            // No package default: that charge stays unclassified rather than borrowing another rule.
            Assert.Equal((int)GstClassification.Unknown, purchase.GetProperty("packageGstClassification").GetInt32());
            Assert.Equal((int)GstClassificationSource.Unknown, purchase.GetProperty("packageGstClassificationSource").GetInt32());
        }
        finally
        {
            await SetSupplierDefaultsAsync(supplierId, productLines: 0, delivery: 0, package: 0);
        }
    }

    [Fact]
    public async Task An_edit_never_applies_a_rule()
    {
        var productId = await _factory.ProductIdAsync(E2ETestFixture.PurchaseProductName);
        var created = await _factory.BusinessA().PostAsync(PurchasesUri, Form(Items(productId, "0")));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var purchaseId = (await EnvelopeMemberAsync(created, "purchase")).GetProperty("id").GetInt32();
        var lineId = (await SingleLineAsync(created)).GetProperty("id").GetInt32();

        await SetProductRuleAsync(productId, GstClassification.Taxable);
        try
        {
            var edited = await _factory.BusinessA().PutAsync(
                $"{PurchasesUri}/{purchaseId}",
                Form(Items(productId, null, id: lineId.ToString()), withFile: false));

            Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
            var line = await SingleLineAsync(edited);
            Assert.Equal((int)GstClassification.Unknown, line.GetProperty("gstClassification").GetInt32());
            Assert.Equal((int)GstClassificationSource.Unknown, line.GetProperty("gstClassificationSource").GetInt32());
        }
        finally
        {
            await SetProductRuleAsync(productId, GstRules.None);
        }
    }

    private async Task SetProductRuleAsync(long productId, GstClassification rule)
    {
        var response = await _factory.BusinessA().PutAsync(
            $"/api/products/{productId}/gst-rule", Json($$"""{"gstRule":{{(int)rule}}}"""));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private async Task SetSupplierDefaultsAsync(int supplierId, int productLines, int delivery, int package)
    {
        var response = await _factory.BusinessA().PutAsync(
            $"/api/suppliers/{supplierId}/gst-defaults",
            Json($$"""{"productLineGstDefault":{{productLines}},"deliveryGstDefault":{{delivery}},"packageGstDefault":{{package}}}"""));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private async Task<int> SupplierIdAsync(string supplierName)
    {
        var response = await _factory.BusinessA().GetAsync("/api/suppliers");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var suppliers = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement
            : document.RootElement.GetProperty("items");
        return suppliers.EnumerateArray()
            .Single(supplier => supplier.GetProperty("name").GetString() == supplierName)
            .GetProperty("id").GetInt32();
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> SingleLineAsync(HttpResponseMessage response) =>
        (await EnvelopeMemberAsync(response, "purchase")).GetProperty("items").EnumerateArray().Single();

    private static async Task<JsonElement> EnvelopeMemberAsync(HttpResponseMessage response, string name)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty(name).Clone();
    }

    private static string Items(long productId, string? gstClassification, string? id = null)
    {
        var fields = new List<string> { $"\"productId\":{productId}", "\"quantity\":2", "\"unitCost\":1.10" };
        if (gstClassification is not null) fields.Add($"\"gstClassification\":{gstClassification}");
        if (id is not null) fields.Add($"\"id\":{id}");
        return $"[{{{string.Join(",", fields)}}}]";
    }

    private static MultipartFormDataContent Form(
        string items,
        string? supplierId = null,
        string? deliveryCost = null,
        string? packageCost = null,
        bool withFile = true)
    {
        var content = new MultipartFormDataContent();
        if (withFile)
        {
            var file = new ByteArrayContent([1, 2, 3]);
            file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            content.Add(file, "file", "scan.jpg");
        }

        content.Add(new StringContent("Rule purchase"), "title");
        content.Add(new StringContent("2026-04-01T10:00:00Z"), "purchaseDate");
        content.Add(new StringContent(items), "items");
        if (supplierId is not null) content.Add(new StringContent(supplierId), "supplierId");
        if (deliveryCost is not null) content.Add(new StringContent(deliveryCost), "deliveryCost");
        if (packageCost is not null) content.Add(new StringContent(packageCost), "packageCost");
        return content;
    }
}
