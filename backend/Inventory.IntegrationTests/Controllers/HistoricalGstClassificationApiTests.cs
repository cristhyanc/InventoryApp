using System.Net;
using System.Text;
using System.Text.Json;
using Inventory.Application.Gst;
using Inventory.Domain.Gst;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using InventoryApi.Auth.E2ETesting;
using InventoryApi.Tests.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// The historical GST classification Preview/Apply maintenance workflow (issue #433) over the real
/// request pipeline: authentication, the required scope, the business-scope middleware, model
/// binding, the controller, the Application use cases, relational SQLite and the registered
/// exception handlers.
///
/// It is hosted rather than unit tested because every criterion the endpoints carry is a pipeline
/// and database fact, not a controller fact: that the preview really persists nothing, that the
/// apply's read, stale check and write are one transaction over a relational database, that a stale
/// preview surfaces as a 400 through <see cref="InventoryApi.Http.DomainExceptionHandler"/> having
/// changed nothing at all, that one business can neither see nor apply another's history, and that
/// classifying history leaves inventory costing and stock untouched. The precedence, the counts and
/// the GST figures themselves are pinned against plain values in
/// <c>HistoricalGstClassificationPolicyTests</c>.
///
/// Every test hosts its own API with its own disposable database, because an apply is a
/// whole-business action: a shared fixture would let one test's applied classification decide
/// another test's preview.
///
/// The seeded history is the situation this workflow exists for - purchases recorded before GST
/// classification existed, so every component is <c>Unknown</c>/<c>Unknown</c> - plus one line a
/// person already classified by hand, one purchase with no supplier, and absent and zero charges.
/// </summary>
public sealed class HistoricalGstClassificationApiTests
{
    private const string PreviewUri = "/api/admin/historical-gst-classification/preview";
    private const string ApplyUri = "/api/admin/historical-gst-classification/apply";

    #region Preview

    [Fact]
    public async Task Preview_reports_the_plan_per_component_kind_and_persists_nothing()
    {
        using var factory = new ApiFactory();
        await factory.ConfigureRulesAsync();
        var before = await factory.PurchaseRowsAsync();

        var preview = await factory.PreviewAsync(factory.BusinessA());

        var summary = preview.GetProperty("summary");
        AssertCounts(summary.GetProperty("productLines"), examined: 3, taxable: 2, gstFree: 1, unknown: 0);
        AssertCounts(summary.GetProperty("deliveryCharges"), examined: 1, taxable: 1, gstFree: 0, unknown: 0);
        AssertCounts(summary.GetProperty("packageCharges"), examined: 1, taxable: 0, gstFree: 0, unknown: 1);
        Assert.Equal(5, summary.GetProperty("componentsExamined").GetInt32());
        Assert.Equal(3, summary.GetProperty("becomingTaxable").GetInt32());
        Assert.Equal(1, summary.GetProperty("becomingGstFree").GetInt32());
        Assert.Equal(1, summary.GetProperty("stayingUnknown").GetInt32());
        Assert.Equal(2, summary.GetProperty("purchasesExamined").GetInt32());
        Assert.Equal(1.74m, summary.GetProperty("lineGst").GetDecimal());
        Assert.Equal(0.50m, summary.GetProperty("chargeGst").GetDecimal());
        Assert.Equal(2.24m, summary.GetProperty("inputGst").GetDecimal());
        Assert.Equal(3.00m, summary.GetProperty("stayingUnknownAmount").GetDecimal());
        Assert.True(summary.GetProperty("classifiesAnything").GetBoolean());
        Assert.Equal(64, preview.GetProperty("fingerprint").GetString()!.Length);

        // Read-only: not one classification, provenance or amount moved.
        Assert.Equal(before, await factory.PurchaseRowsAsync());
    }

    /// <summary>
    /// The preview never returns the component writes it stands for. Only the summary and the
    /// fingerprint cross the boundary, so there is no classification or provenance for a caller to
    /// edit and send back.
    /// </summary>
    [Fact]
    public async Task Preview_exposes_only_the_summary_and_the_fingerprint()
    {
        using var factory = new ApiFactory();
        await factory.ConfigureRulesAsync();

        var preview = await factory.PreviewAsync(factory.BusinessA());

        Assert.Equal(
            ["summary", "fingerprint"],
            preview.EnumerateObject().Select(property => property.Name));
    }

    #endregion

    #region Apply

    [Fact]
    public async Task Apply_writes_exactly_the_previewed_classifications_with_their_provenance()
    {
        using var factory = new ApiFactory();
        await factory.ConfigureRulesAsync();
        var preview = await factory.PreviewAsync(factory.BusinessA());

        var applied = await factory.ApplyAsync(
            factory.BusinessA(), preview.GetProperty("fingerprint").GetString()!);

        Assert.Equal(HttpStatusCode.OK, applied.Status);
        Assert.Equal(4, applied.Body.GetProperty("componentsClassified").GetInt32());
        Assert.Equal(
            preview.GetProperty("summary").GetRawText(),
            applied.Body.GetProperty("summary").GetRawText());

        await using var db = factory.Database();
        // A product rule classified this line; the supplier's GST-free product-line default did not.
        Assert.Equal(
            (GstClassification.Taxable, GstClassificationSource.ProductRule),
            await factory.LineStateAsync(db, E2ETestFixture.PurchaseProductName, 1.21m));
        // No product rule, so the supplier's product-line default classified it.
        Assert.Equal(
            (GstClassification.GstFree, GstClassificationSource.SupplierDefault),
            await factory.LineStateAsync(db, E2ETestFixture.CorrectionProductName, 2.00m));
        // The supplier-less purchase's line still follows the product's own rule.
        Assert.Equal(
            (GstClassification.Taxable, GstClassificationSource.ProductRule),
            await factory.LineStateAsync(db, E2ETestFixture.PurchaseProductName, 7.00m));

        var purchase = await factory.PurchaseAsync(db, ApiFactory.SuppliedPurchaseTitle);
        Assert.Equal(GstClassification.Taxable, purchase.DeliveryGstClassification);
        Assert.Equal(GstClassificationSource.SupplierFeeDefault, purchase.DeliveryGstClassificationSource);
        // No package fee default is configured, so the package charge stays visibly unresolved
        // rather than being treated as GST-free.
        Assert.Equal(GstClassification.Unknown, purchase.PackageGstClassification);
        Assert.Equal(GstClassificationSource.Unknown, purchase.PackageGstClassificationSource);
    }

    /// <summary>
    /// A manual classification always wins. The hand-classified line's product carries a GST-free
    /// rule precisely so that a reclassification would be visible if one happened.
    /// </summary>
    [Fact]
    public async Task Apply_never_changes_a_manual_classification()
    {
        using var factory = new ApiFactory();
        await factory.ConfigureRulesAsync();
        var preview = await factory.PreviewAsync(factory.BusinessA());

        await factory.ApplyAsync(factory.BusinessA(), preview.GetProperty("fingerprint").GetString()!);

        await using var db = factory.Database();
        Assert.Equal(
            (GstClassification.Taxable, GstClassificationSource.Manual),
            await factory.LineStateAsync(db, E2ETestFixture.ReorderProductName, 9.90m));
    }

    /// <summary>
    /// Classification is accounting data only: an apply must not move a unit cost, a purchase
    /// amount, physical stock, costing quantity, inventory value, the weighted-average cost or a
    /// stock movement (AGENTS.md § Purchase GST classification).
    /// </summary>
    [Fact]
    public async Task Apply_changes_no_amount_no_costing_and_no_stock_movement()
    {
        using var factory = new ApiFactory();
        await factory.ConfigureRulesAsync();
        var amountsBefore = await factory.PurchaseAmountsAsync();
        var costingBefore = await factory.CostingRowsAsync();
        var preview = await factory.PreviewAsync(factory.BusinessA());

        await factory.ApplyAsync(factory.BusinessA(), preview.GetProperty("fingerprint").GetString()!);

        Assert.Equal(amountsBefore, await factory.PurchaseAmountsAsync());
        Assert.Equal(costingBefore, await factory.CostingRowsAsync());
    }

    /// <summary>
    /// Decision D3 through the database: an absent or zero charge has no classification, is not
    /// examined and is not written - the supplier-less purchase's null delivery and zero package
    /// charge stay exactly as they were.
    /// </summary>
    [Fact]
    public async Task Apply_classifies_no_absent_or_zero_charge()
    {
        using var factory = new ApiFactory();
        await factory.ConfigureRulesAsync(packageDefault: GstClassification.Taxable);
        var preview = await factory.PreviewAsync(factory.BusinessA());

        await factory.ApplyAsync(factory.BusinessA(), preview.GetProperty("fingerprint").GetString()!);

        await using var db = factory.Database();
        var purchase = await factory.PurchaseAsync(db, ApiFactory.SupplierlessPurchaseTitle);
        Assert.Null(purchase.DeliveryCost);
        Assert.Equal(0m, purchase.PackageCost);
        Assert.Equal(GstClassification.Unknown, purchase.DeliveryGstClassification);
        Assert.Equal(GstClassificationSource.Unknown, purchase.DeliveryGstClassificationSource);
        Assert.Equal(GstClassification.Unknown, purchase.PackageGstClassification);
        Assert.Equal(GstClassificationSource.Unknown, purchase.PackageGstClassificationSource);
    }

    [Fact]
    public async Task Re_running_preview_and_apply_after_an_apply_changes_nothing()
    {
        using var factory = new ApiFactory();
        await factory.ConfigureRulesAsync();
        var first = await factory.PreviewAsync(factory.BusinessA());
        await factory.ApplyAsync(factory.BusinessA(), first.GetProperty("fingerprint").GetString()!);
        var afterFirstApply = await factory.PurchaseRowsAsync();

        var second = await factory.PreviewAsync(factory.BusinessA());
        var reapplied = await factory.ApplyAsync(
            factory.BusinessA(), second.GetProperty("fingerprint").GetString()!);

        var summary = second.GetProperty("summary");
        // Only the package charge no default covers is still unclassified, and it stays that way.
        Assert.Equal(1, summary.GetProperty("componentsExamined").GetInt32());
        Assert.Equal(1, summary.GetProperty("stayingUnknown").GetInt32());
        Assert.Equal(0m, summary.GetProperty("inputGst").GetDecimal());
        Assert.False(summary.GetProperty("classifiesAnything").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, reapplied.Status);
        Assert.Equal(0, reapplied.Body.GetProperty("componentsClassified").GetInt32());
        Assert.Equal(afterFirstApply, await factory.PurchaseRowsAsync());
    }

    #endregion

    #region Stale and tampered previews

    /// <summary>
    /// The stale-preview guarantee, with the change landing between the preview and the apply: a
    /// purchase line is classified by hand in between, so the apply refuses and updates nothing at
    /// all - no partial application of the components that were still eligible.
    /// </summary>
    [Fact]
    public async Task A_preview_made_stale_by_a_concurrent_classification_is_refused_and_updates_nothing()
    {
        using var factory = new ApiFactory();
        await factory.ConfigureRulesAsync();
        var preview = await factory.PreviewAsync(factory.BusinessA());
        await factory.ClassifyLineByHandAsync(E2ETestFixture.CorrectionProductName, 2.00m);
        var before = await factory.PurchaseRowsAsync();

        var refused = await factory.ApplyAsync(
            factory.BusinessA(), preview.GetProperty("fingerprint").GetString()!);

        AssertStalePreview(refused);
        Assert.Equal(before, await factory.PurchaseRowsAsync());
    }

    /// <summary>
    /// The same refusal when a purchase amount changed: the GST figure the operator approved no
    /// longer describes the data that would be classified.
    /// </summary>
    [Fact]
    public async Task A_preview_made_stale_by_a_purchase_edit_is_refused_and_updates_nothing()
    {
        using var factory = new ApiFactory();
        await factory.ConfigureRulesAsync();
        var preview = await factory.PreviewAsync(factory.BusinessA());
        await factory.ChangeDeliveryCostAsync(ApiFactory.SuppliedPurchaseTitle, 6.00m);
        var before = await factory.PurchaseRowsAsync();

        var refused = await factory.ApplyAsync(
            factory.BusinessA(), preview.GetProperty("fingerprint").GetString()!);

        AssertStalePreview(refused);
        Assert.Equal(before, await factory.PurchaseRowsAsync());
    }

    /// <summary>
    /// And when a configured rule changed, through the rule endpoints of issue #430: the rules are
    /// relevant source data, so a default configured in between invalidates the preview rather than
    /// silently classifying components the operator never saw.
    /// </summary>
    [Fact]
    public async Task A_preview_made_stale_by_a_rule_change_is_refused_and_updates_nothing()
    {
        using var factory = new ApiFactory();
        await factory.ConfigureRulesAsync();
        var preview = await factory.PreviewAsync(factory.BusinessA());
        await factory.ConfigureRulesAsync(packageDefault: GstClassification.Taxable);
        var before = await factory.PurchaseRowsAsync();

        var refused = await factory.ApplyAsync(
            factory.BusinessA(), preview.GetProperty("fingerprint").GetString()!);

        AssertStalePreview(refused);
        Assert.Equal(before, await factory.PurchaseRowsAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-fingerprint")]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000")]
    public async Task A_tampered_fingerprint_is_refused_and_updates_nothing(string fingerprint)
    {
        using var factory = new ApiFactory();
        await factory.ConfigureRulesAsync();
        await factory.PreviewAsync(factory.BusinessA());
        var before = await factory.PurchaseRowsAsync();

        var refused = await factory.ApplyAsync(factory.BusinessA(), fingerprint);

        AssertStalePreview(refused);
        Assert.Equal(before, await factory.PurchaseRowsAsync());
    }

    /// <summary>
    /// A body that carries extra members - counts, totals, a component list, a business id - changes
    /// nothing: the apply binds only the fingerprint and derives everything else from its own read.
    /// Here the fingerprint is the real one, so the apply succeeds, and it succeeds with exactly the
    /// server's own plan rather than the inflated figures the body claimed.
    /// </summary>
    [Fact]
    public async Task A_body_that_claims_its_own_counts_totals_and_owner_is_ignored()
    {
        using var factory = new ApiFactory();
        await factory.ConfigureRulesAsync();
        var preview = await factory.PreviewAsync(factory.BusinessA());
        var fingerprint = preview.GetProperty("fingerprint").GetString()!;

        var applied = await factory.PostAsync(factory.BusinessA(), ApplyUri, $$"""
            {"fingerprint":"{{fingerprint}}","businessId":2,"componentsClassified":99,
             "summary":{"lineGst":999.99,"becomingTaxable":99},
             "changes":[{"kind":0,"purchaseId":1,"lineId":1,"classification":1,"source":1}]}
            """);

        Assert.Equal(HttpStatusCode.OK, applied.Status);
        Assert.Equal(4, applied.Body.GetProperty("componentsClassified").GetInt32());
        Assert.Equal(
            preview.GetProperty("summary").GetRawText(),
            applied.Body.GetProperty("summary").GetRawText());
    }

    #endregion

    #region Tenant isolation

    /// <summary>
    /// Tenant isolation through the real pipeline. Each business previews only its own purchase
    /// history - the two plans are deliberately different - and neither can apply the other's
    /// preview, because the resolved business is part of the fingerprint.
    /// </summary>
    [Fact]
    public async Task One_business_previews_only_its_own_history_and_cannot_apply_the_others_preview()
    {
        using var factory = new ApiFactory();
        await factory.ConfigureRulesAsync();
        var businessA = await factory.PreviewAsync(factory.BusinessA());
        var businessB = await factory.PreviewAsync(factory.BusinessB());
        var before = await factory.PurchaseRowsAsync();

        Assert.Equal(5, businessA.GetProperty("summary").GetProperty("componentsExamined").GetInt32());
        Assert.Equal(2.24m, businessA.GetProperty("summary").GetProperty("inputGst").GetDecimal());
        Assert.Equal(2, businessB.GetProperty("summary").GetProperty("componentsExamined").GetInt32());
        Assert.Equal(2.00m, businessB.GetProperty("summary").GetProperty("inputGst").GetDecimal());
        Assert.NotEqual(
            businessA.GetProperty("fingerprint").GetString(),
            businessB.GetProperty("fingerprint").GetString());

        var foreignIntoB = await factory.ApplyAsync(
            factory.BusinessB(), businessA.GetProperty("fingerprint").GetString()!);
        var foreignIntoA = await factory.ApplyAsync(
            factory.BusinessA(), businessB.GetProperty("fingerprint").GetString()!);

        AssertStalePreview(foreignIntoB);
        AssertStalePreview(foreignIntoA);
        Assert.Equal(before, await factory.PurchaseRowsAsync());
    }

    /// <summary>
    /// One business applying its own preview classifies only its own components: the other
    /// business's purchase is byte-identical afterwards.
    /// </summary>
    [Fact]
    public async Task An_apply_classifies_nothing_in_another_business()
    {
        using var factory = new ApiFactory();
        await factory.ConfigureRulesAsync();
        var businessBBefore = await factory.PurchaseRowsAsync(ApiFactory.BusinessBPurchaseTitle);
        var preview = await factory.PreviewAsync(factory.BusinessA());

        await factory.ApplyAsync(factory.BusinessA(), preview.GetProperty("fingerprint").GetString()!);

        Assert.Equal(businessBBefore, await factory.PurchaseRowsAsync(ApiFactory.BusinessBPurchaseTitle));
    }

    [Fact]
    public async Task An_unauthenticated_caller_reaches_neither_endpoint()
    {
        using var factory = new ApiFactory();

        var preview = await factory.PostAsync(factory.Anonymous(), PreviewUri, "{}");
        var apply = await factory.PostAsync(factory.Anonymous(), ApplyUri, """{"fingerprint":"x"}""");

        Assert.Equal(HttpStatusCode.Unauthorized, preview.Status);
        Assert.Equal(HttpStatusCode.Unauthorized, apply.Status);
    }

    #endregion

    private static void AssertCounts(JsonElement counts, int examined, int taxable, int gstFree, int unknown)
    {
        Assert.Equal(examined, counts.GetProperty("examined").GetInt32());
        Assert.Equal(taxable, counts.GetProperty("becomingTaxable").GetInt32());
        Assert.Equal(gstFree, counts.GetProperty("becomingGstFree").GetInt32());
        Assert.Equal(unknown, counts.GetProperty("stayingUnknown").GetInt32());
    }

    /// <summary>
    /// The refusal contract: a 400 carrying the Application's own caller-safe message in the
    /// <c>message</c> extension the Angular client reads, not a 409 and not a generic 500.
    /// </summary>
    private static void AssertStalePreview((HttpStatusCode Status, JsonElement Body) response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.Status);
        Assert.Equal(
            ApplyHistoricalGstClassification.StalePreviewMessage,
            response.Body.GetProperty("message").GetString());
    }

    /// <summary>
    /// The API hosted the way the end-to-end harness hosts it - the dedicated E2E environment, its
    /// two synthetic businesses with their own products and suppliers, and a disposable relational
    /// database - plus the unclassified purchase history this workflow exists to classify.
    /// </summary>
    public sealed class ApiFactory : WebApplicationFactory<Program>
    {
        internal const string SuppliedPurchaseTitle = "E2E unclassified supplied purchase";
        internal const string SupplierlessPurchaseTitle = "E2E unclassified purchase with no supplier";
        internal const string BusinessBPurchaseTitle = "E2E business B unclassified purchase";

        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        private bool _seeded;

        public ApiFactory()
        {
            // Hosting must not depend on the process's current directory; see E2ETestHostContentRoot.
            E2ETestHostContentRoot.Pin();
            _connection.Open();
        }

        public HttpClient BusinessA() => As(E2ETestActors.BusinessAOwner.Key);

        public HttpClient BusinessB() => As(E2ETestActors.BusinessBOwner.Key);

        public HttpClient Anonymous() => CreateClient();

        /// <summary>
        /// Configures the rules this workflow reads, through the issue #430 rule endpoints rather
        /// than by writing columns, so the two features are exercised together. Business A's
        /// purchased water carries a taxable product rule, its hand-classified line's product
        /// carries a GST-free one (which must never reach that line), and supplier A defaults to
        /// GST-free product lines and a taxable delivery charge.
        /// </summary>
        public async Task ConfigureRulesAsync(GstClassification packageDefault = GstRules.None)
        {
            await SeedPurchasesAsync();

            var businessA = BusinessA();
            await PutAsync(
                businessA,
                $"/api/products/{await ProductIdAsync(E2ETestFixture.PurchaseProductName)}/gst-rule",
                $$"""{"gstRule":{{(int)GstClassification.Taxable}}}""");
            await PutAsync(
                businessA,
                $"/api/products/{await ProductIdAsync(E2ETestFixture.ReorderProductName)}/gst-rule",
                $$"""{"gstRule":{{(int)GstClassification.GstFree}}}""");
            await PutAsync(
                businessA,
                $"/api/suppliers/{await SupplierIdAsync(E2ETestFixture.SupplierAName)}/gst-defaults",
                $$"""
                {"productLineGstDefault":{{(int)GstClassification.GstFree}},
                 "deliveryGstDefault":{{(int)GstClassification.Taxable}},
                 "packageGstDefault":{{(int)packageDefault}}}
                """);
            await PutAsync(
                BusinessB(),
                $"/api/suppliers/{await SupplierIdAsync(E2ETestFixture.SupplierBName)}/gst-defaults",
                $$"""
                {"productLineGstDefault":{{(int)GstClassification.Taxable}},
                 "deliveryGstDefault":{{(int)GstClassification.GstFree}},
                 "packageGstDefault":{{(int)GstRules.None}}}
                """);
        }

        public async Task<JsonElement> PreviewAsync(HttpClient client)
        {
            var response = await PostAsync(client, PreviewUri, "{}");

            Assert.Equal(HttpStatusCode.OK, response.Status);
            return response.Body;
        }

        public Task<(HttpStatusCode Status, JsonElement Body)> ApplyAsync(HttpClient client, string fingerprint) =>
            PostAsync(client, ApplyUri, JsonSerializer.Serialize(new { fingerprint }));

        public async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(
            HttpClient client, string uri, string body)
        {
            var response = await client.PostAsync(uri, Json(body));
            var text = await response.Content.ReadAsStringAsync();
            return (response.StatusCode, text.Length == 0 ? default : Parse(text));
        }

        /// <summary>
        /// Every persisted purchase and purchase line of both businesses, with its GST classification
        /// and provenance, as one comparable value - so "the preview wrote nothing" and "the refused
        /// apply wrote nothing" are shown rather than assumed. <paramref name="titleFilter"/> narrows
        /// it to one purchase when a test needs the other business's rows alone.
        /// </summary>
        public async Task<string> PurchaseRowsAsync(string? titleFilter = null)
        {
            await SeedPurchasesAsync();

            await using var db = Database();
            var purchases = await db.Receipts.AsNoTracking()
                .Where(purchase => titleFilter == null || purchase.Title == titleFilter)
                .OrderBy(purchase => purchase.Id)
                .Select(purchase =>
                    $"p{purchase.Id}:{purchase.BusinessId}:{purchase.Title}:{purchase.DeliveryCost}:" +
                    $"{purchase.DeliveryGstClassification}:{purchase.DeliveryGstClassificationSource}:" +
                    $"{purchase.PackageCost}:{purchase.PackageGstClassification}:" +
                    $"{purchase.PackageGstClassificationSource}")
                .ToListAsync();
            var items = await db.ReceiptItems.AsNoTracking()
                .Where(item => titleFilter == null || item.Purchase!.Title == titleFilter)
                .OrderBy(item => item.Id)
                .Select(item =>
                    $"l{item.Id}:{item.BusinessId}:{item.ReceiptId}:{item.ProductId}:{item.Quantity}:" +
                    $"{item.UnitCost}:{item.GstClassification}:{item.GstClassificationSource}")
                .ToListAsync();
            return string.Join("|", purchases.Concat(items));
        }

        /// <summary>Every purchase and line amount, with no classification: what an apply must leave alone.</summary>
        public async Task<string> PurchaseAmountsAsync()
        {
            await SeedPurchasesAsync();

            await using var db = Database();
            var purchases = await db.Receipts.AsNoTracking().OrderBy(purchase => purchase.Id)
                .Select(purchase =>
                    $"p{purchase.Id}:{purchase.TotalAmount}:{purchase.DeliveryCost}:{purchase.PackageCost}:" +
                    $"{purchase.PurchaseDate:O}:{purchase.SupplierId}")
                .ToListAsync();
            var items = await db.ReceiptItems.AsNoTracking().OrderBy(item => item.Id)
                .Select(item => $"l{item.Id}:{item.ProductId}:{item.Quantity}:{item.UnitCost}")
                .ToListAsync();
            return string.Join("|", purchases.Concat(items));
        }

        /// <summary>
        /// Every costing and physical-stock value an apply must not touch, plus the stock movement
        /// history: AVCO, costing quantity, inventory value, home stock and the stock adjustments.
        /// </summary>
        public async Task<string> CostingRowsAsync()
        {
            await SeedPurchasesAsync();

            await using var db = Database();
            var products = await db.Products.AsNoTracking().OrderBy(product => product.Id)
                .Select(product =>
                    $"{product.Id}:{product.QuantityInStock}:{product.CostingQuantity}:" +
                    $"{product.InventoryValue}:{product.AverageUnitCost}:{product.UnitPrice}")
                .ToListAsync();
            var adjustments = await db.StockAdjustments.AsNoTracking().OrderBy(adjustment => adjustment.Id)
                .Select(adjustment =>
                    $"{adjustment.Id}:{adjustment.ProductId}:{adjustment.QuantityChange}:{adjustment.Reason}")
                .ToListAsync();
            return string.Join("|", products.Concat(adjustments));
        }

        public async Task<Purchase> PurchaseAsync(AppDbContext db, string title) =>
            await db.Receipts.AsNoTracking().SingleAsync(purchase => purchase.Title == title);

        /// <summary>One seeded line's stored classification state, found by its product and unit cost.</summary>
        public async Task<(GstClassification, GstClassificationSource)> LineStateAsync(
            AppDbContext db, string productName, decimal unitCost)
        {
            var productId = await ProductIdAsync(productName);
            var item = await db.ReceiptItems.AsNoTracking()
                .SingleAsync(line => line.ProductId == productId && line.UnitCost == unitCost);
            return (item.GstClassification, item.GstClassificationSource);
        }

        /// <summary>
        /// A competing write landing after a preview was taken: a person classifies one line by
        /// hand. Written outside the boundary on purpose - the race is a database race, not an
        /// HTTP one.
        /// </summary>
        public async Task ClassifyLineByHandAsync(string productName, decimal unitCost)
        {
            var productId = await ProductIdAsync(productName);

            await using var db = Database();
            var item = await db.ReceiptItems
                .SingleAsync(line => line.ProductId == productId && line.UnitCost == unitCost);
            item.GstClassification = GstClassification.GstFree;
            item.GstClassificationSource = GstClassificationSource.Manual;
            await db.SaveChangesAsync();
        }

        /// <summary>A competing edit of the purchase data the preview's GST figure was derived from.</summary>
        public async Task ChangeDeliveryCostAsync(string title, decimal deliveryCost)
        {
            await using var db = Database();
            var purchase = await db.Receipts.SingleAsync(candidate => candidate.Title == title);
            purchase.DeliveryCost = deliveryCost;
            await db.SaveChangesAsync();
        }

        public async Task<long> ProductIdAsync(string productName)
        {
            await using var db = Database();
            return (await db.Products.AsNoTracking().SingleAsync(product => product.Name == productName)).Id;
        }

        public async Task<int> SupplierIdAsync(string supplierName)
        {
            await using var db = Database();
            return (await db.Suppliers.AsNoTracking().SingleAsync(supplier => supplier.Name == supplierName)).Id;
        }

        /// <summary>
        /// An unrestricted context for arranging and verifying both businesses' rows from outside the
        /// boundary, never over HTTP. Resolving <c>Services</c> starts the host, which is what
        /// creates the schema and seeds the fixture this then reads.
        /// </summary>
        public AppDbContext Database()
        {
            _ = Services;
            return TestAppDbContext.Unrestricted(
                new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(E2ETestEnvironment.EnvironmentName);

            // The E2E environment is not one of the environments that migrate automatically, so
            // this disposable database opts in with the setting that exists for exactly that case.
            builder.UseSetting("Database:AllowAutomaticMigrationUnsafeOutsideDevelopment", "true");

            builder.ConfigureServices(services =>
            {
                var dbContextOptions = services.SingleOrDefault(
                    descriptor => descriptor.ServiceType == typeof(DbContextOptions<AppDbContext>));
                if (dbContextOptions is not null)
                {
                    services.Remove(dbContextOptions);
                }

                services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                _connection.Dispose();
            }
        }

        /// <summary>
        /// The purchase history this workflow exists for: recorded before classification existed, so
        /// every component is <c>Unknown</c>/<c>Unknown</c>. The E2E fixture seeds no purchase, so
        /// without this there would be nothing to classify and nothing an assertion could prove.
        ///
        /// It deliberately includes the cases the acceptance criteria name: a line whose product has
        /// its own rule, a line that can only fall back to the supplier default, a line a person
        /// already classified by hand, a present delivery charge and a present package charge, a
        /// purchase with no supplier at all, and absent and zero charges. No stock movement or
        /// costing history is recorded, because this is a GST classification fixture: the costing
        /// snapshot has to stay comparable across an apply.
        /// </summary>
        private async Task SeedPurchasesAsync()
        {
            if (_seeded) return;
            _seeded = true;

            await using var db = Database();
            var water = await db.Products.AsNoTracking()
                .SingleAsync(product => product.Name == E2ETestFixture.PurchaseProductName);
            var bars = await db.Products.AsNoTracking()
                .SingleAsync(product => product.Name == E2ETestFixture.CorrectionProductName);
            var chips = await db.Products.AsNoTracking()
                .SingleAsync(product => product.Name == E2ETestFixture.ReorderProductName);
            var chocolate = await db.Products.AsNoTracking()
                .SingleAsync(product => product.Name == E2ETestFixture.BusinessBProductName);
            var supplierA = await db.Suppliers.AsNoTracking()
                .SingleAsync(supplier => supplier.Name == E2ETestFixture.SupplierAName);
            var supplierB = await db.Suppliers.AsNoTracking()
                .SingleAsync(supplier => supplier.Name == E2ETestFixture.SupplierBName);

            db.Receipts.Add(NewPurchase(
                water.BusinessId,
                SuppliedPurchaseTitle,
                supplierA.Id,
                deliveryCost: 5.50m,
                packageCost: 3.00m,
                items:
                [
                    NewItem(water.BusinessId, water.Id, 10m, 1.21m),
                    NewItem(bars.BusinessId, bars.Id, 4m, 2.00m),
                    // Already classified by a person: the apply must leave it exactly here, even
                    // though its product carries a GST-free rule.
                    NewItem(
                        chips.BusinessId,
                        chips.Id,
                        1m,
                        9.90m,
                        GstClassification.Taxable,
                        GstClassificationSource.Manual),
                ]));
            db.Receipts.Add(NewPurchase(
                water.BusinessId,
                SupplierlessPurchaseTitle,
                supplierId: null,
                deliveryCost: null,
                packageCost: 0m,
                items: [NewItem(water.BusinessId, water.Id, 1m, 7.00m)]));
            db.Receipts.Add(NewPurchase(
                chocolate.BusinessId,
                BusinessBPurchaseTitle,
                supplierB.Id,
                deliveryCost: 4.00m,
                packageCost: null,
                items: [NewItem(chocolate.BusinessId, chocolate.Id, 2m, 11.00m)]));

            await db.SaveChangesAsync();
        }

        private static Purchase NewPurchase(
            int businessId,
            string title,
            int? supplierId,
            decimal? deliveryCost,
            decimal? packageCost,
            List<PurchaseItem> items)
        {
            var seededAt = new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc);
            return new Purchase
            {
                BusinessId = businessId,
                Title = title,
                SupplierId = supplierId,
                TotalAmount = items.Sum(item => item.Quantity * item.UnitCost)
                    + (deliveryCost ?? 0m) + (packageCost ?? 0m),
                DeliveryCost = deliveryCost,
                PackageCost = packageCost,
                PurchaseDate = seededAt,
                FileName = "scan.jpg",
                StoredFileName = $"{title}.jpg",
                ContentType = "image/jpeg",
                FileSizeBytes = 2048,
                CreatedAt = seededAt,
                Items = items,
            };
        }

        private static PurchaseItem NewItem(
            int businessId,
            long productId,
            decimal quantity,
            decimal unitCost,
            GstClassification classification = GstClassification.Unknown,
            GstClassificationSource source = GstClassificationSource.Unknown) =>
            new()
            {
                BusinessId = businessId,
                ProductId = productId,
                Quantity = quantity,
                UnitCost = unitCost,
                GstClassification = classification,
                GstClassificationSource = source,
            };

        private static async Task PutAsync(HttpClient client, string uri, string body)
        {
            var response = await client.PutAsync(uri, Json(body));

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

        private static JsonElement Parse(string json)
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }

        private HttpClient As(string actorKey)
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.TryAddWithoutValidation(E2ETestActors.ActorHeaderName, actorKey);
            return client;
        }
    }
}
