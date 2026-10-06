using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Inventory.Application.Documents;
using Inventory.Domain.Gst;
using Inventory.Domain.Purchases;
using Inventory.Infrastructure.Data;
using InventoryApi.Auth.E2ETesting;
using InventoryApi.Tests.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// The purchase GST classification contract over the real HTTP pipeline (issue #429): a genuine
/// <c>multipart/form-data</c> body, the framework's own form-field binding, the controller's JSON
/// deserialization of the <c>items</c> field, authentication, the business-scope middleware and
/// relational SQLite.
///
/// It exists because the defect it pins is a model-binding defect. A C# enum is only a compile-time
/// constraint: both <c>deliveryGstClassification=999</c> as a form field and
/// <c>"gstClassification": 999</c> inside the items JSON bind to a <see cref="GstClassification"/>
/// value no rule describes, and nothing in a directly constructed controller call proves what
/// binding does with them. Calling <c>PurchasesController.Upload(...)</c> in-process hands the
/// classification over already typed, so it cannot show that an undefined integer gets this far -
/// or that the request is refused with <c>400</c> once it does.
///
/// Both synthetic E2E businesses are used, so a purchase line id - which the update contract now
/// accepts from the caller - is shown to be unusable across the tenant boundary through the real
/// request pipeline rather than through a hand-written business id.
/// </summary>
public sealed class PurchaseGstClassificationApiTests : IClassFixture<PurchaseGstClassificationApiTests.ApiFactory>
{
    private const string PurchasesUri = "/api/purchases";

    private readonly ApiFactory _factory;

    public PurchaseGstClassificationApiTests(ApiFactory factory) => _factory = factory;

    /// <summary>
    /// The control case: a supported classification survives the whole binding path, on the form
    /// fields and inside the items JSON alike, and comes back with provenance <c>Manual</c>. Without
    /// it, a blanket <c>400</c> would look like correct validation.
    /// </summary>
    [Fact]
    public async Task A_supported_classification_binds_from_the_multipart_body_and_round_trips()
    {
        var productId = await _factory.ProductIdAsync(E2ETestFixture.PurchaseProductName);

        var response = await _factory.BusinessA().PostAsync(PurchasesUri, Form(
            items: Items(productId, gstClassification: "1"),
            deliveryCost: "5.00",
            deliveryGstClassification: "1",
            packageCost: "2.00",
            packageGstClassification: "2"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var purchase = await PurchaseAsync(response);
        Assert.Equal((int)GstClassification.Taxable, purchase.GetProperty("deliveryGstClassification").GetInt32());
        Assert.Equal((int)GstClassificationSource.Manual, purchase.GetProperty("deliveryGstClassificationSource").GetInt32());
        Assert.Equal((int)GstClassification.GstFree, purchase.GetProperty("packageGstClassification").GetInt32());
        Assert.Equal((int)GstClassificationSource.Manual, purchase.GetProperty("packageGstClassificationSource").GetInt32());
        var line = purchase.GetProperty("items").EnumerateArray().Single();
        Assert.Equal((int)GstClassification.Taxable, line.GetProperty("gstClassification").GetInt32());
        Assert.Equal((int)GstClassificationSource.Manual, line.GetProperty("gstClassificationSource").GetInt32());
    }

    /// <summary>A body with no classification at all - every existing client's shape - still works.</summary>
    [Fact]
    public async Task A_body_that_omits_every_classification_is_still_accepted_as_unknown()
    {
        var productId = await _factory.ProductIdAsync(E2ETestFixture.PurchaseProductName);

        var response = await _factory.BusinessA().PostAsync(PurchasesUri, Form(
            items: Items(productId, gstClassification: null), deliveryCost: "5.00"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var purchase = await PurchaseAsync(response);
        Assert.Equal((int)GstClassification.Unknown, purchase.GetProperty("deliveryGstClassification").GetInt32());
        Assert.Equal(
            (int)GstClassification.Unknown,
            purchase.GetProperty("items").EnumerateArray().Single().GetProperty("gstClassification").GetInt32());
    }

    /// <summary>
    /// The hole the repair closes. The <c>items</c> field is a JSON string the controller
    /// deserializes itself, and <c>System.Text.Json</c> maps any number to the enum, so this is the
    /// one classification an undefined integer genuinely reaches the application through. It is
    /// refused with the Domain's own message, and no purchase, line, movement or document is
    /// written.
    /// </summary>
    [Theory]
    [InlineData("999")]
    [InlineData("-1")]
    [InlineData("3")]
    public async Task An_undefined_line_classification_in_the_items_json_is_refused(string value)
    {
        var productId = await _factory.ProductIdAsync(E2ETestFixture.PurchaseProductName);
        var purchasesBefore = await _factory.PurchaseRowsAsync();
        var documentsBefore = _factory.DocumentSaveCount;

        var response = await _factory.BusinessA().PostAsync(PurchasesUri, Form(
            items: Items(productId, gstClassification: value)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            PurchaseGstPolicy.UnsupportedClassificationMessage,
            await response.Content.ReadAsStringAsync());
        Assert.Equal(purchasesBefore, await _factory.PurchaseRowsAsync());
        Assert.Equal(documentsBefore, _factory.DocumentSaveCount);
    }

    /// <summary>
    /// The charge classifications arrive as ordinary form fields, which the framework's own enum
    /// binding already refuses when the value is not a declared member - so these never reach the
    /// Application guard and are answered as a binding validation failure instead. The guard still
    /// covers them (see <c>PurchaseGstPolicyTests</c> and <c>PurchaseGstClassificationTests</c>),
    /// which is the point of checking here: whichever layer catches it, an undefined charge
    /// classification is a 400 that stores nothing.
    /// </summary>
    [Theory]
    [InlineData("999")]
    [InlineData("-1")]
    public async Task An_undefined_delivery_or_package_classification_form_field_is_refused(string value)
    {
        var productId = await _factory.ProductIdAsync(E2ETestFixture.PurchaseProductName);
        var purchasesBefore = await _factory.PurchaseRowsAsync();
        var documentsBefore = _factory.DocumentSaveCount;

        var delivery = await _factory.BusinessA().PostAsync(PurchasesUri, Form(
            items: Items(productId, gstClassification: null),
            deliveryCost: "5.00",
            deliveryGstClassification: value));
        var package = await _factory.BusinessA().PostAsync(PurchasesUri, Form(
            items: Items(productId, gstClassification: null),
            packageCost: "2.00",
            packageGstClassification: value));

        Assert.Equal(HttpStatusCode.BadRequest, delivery.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, package.StatusCode);
        Assert.Equal(purchasesBefore, await _factory.PurchaseRowsAsync());
        Assert.Equal(documentsBefore, _factory.DocumentSaveCount);
    }

    /// <summary>
    /// The edit path binds the same fields from the same kind of body, and must refuse the same
    /// values without touching the purchase it was asked to change.
    /// </summary>
    [Theory]
    [InlineData("999")]
    [InlineData("-1")]
    public async Task An_undefined_classification_is_refused_on_update_and_changes_nothing(string value)
    {
        var purchaseId = await CreatePurchaseAsync(gstClassification: "1");
        var productId = await _factory.ProductIdAsync(E2ETestFixture.PurchaseProductName);
        var before = await _factory.PurchaseRowsAsync();

        var line = await _factory.BusinessA().PutAsync($"{PurchasesUri}/{purchaseId}", Form(
            items: Items(productId, gstClassification: value), withFile: false));
        var charge = await _factory.BusinessA().PutAsync($"{PurchasesUri}/{purchaseId}", Form(
            items: Items(productId, gstClassification: null),
            deliveryCost: "9.00",
            deliveryGstClassification: value,
            withFile: false));

        Assert.Equal(HttpStatusCode.BadRequest, line.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, charge.StatusCode);
        Assert.Equal(PurchaseGstPolicy.UnsupportedClassificationMessage, await line.Content.ReadAsStringAsync());
        Assert.Equal(before, await _factory.PurchaseRowsAsync());
    }

    /// <summary>
    /// The update contract now accepts a purchase line id from the caller, so it has to be shown
    /// that one business cannot name another business's line. Business A's line id is resolved
    /// through A's own authenticated read, and B then submits it on B's own purchase: the tenant
    /// query filters mean that line is simply not among B's purchase's lines, so it is refused as
    /// unknown and nothing on either side changes.
    /// </summary>
    [Fact]
    public async Task One_business_cannot_name_another_businesss_purchase_line_id()
    {
        var businessALineId = await FirstLineIdAsync(await CreatePurchaseAsync(gstClassification: "1"));
        var businessBProductId = await _factory.ProductIdAsync(E2ETestFixture.BusinessBProductName);
        var businessBPurchaseId = await CreatePurchaseAsync(
            gstClassification: "1", client: _factory.BusinessB(), productId: businessBProductId);
        var before = await _factory.PurchaseRowsAsync();

        var response = await _factory.BusinessB().PutAsync($"{PurchasesUri}/{businessBPurchaseId}", Form(
            items: Items(businessBProductId, gstClassification: null, id: businessALineId.ToString()),
            withFile: false));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            PurchaseLineIdentityPolicy.UnknownLineIdMessage,
            await response.Content.ReadAsStringAsync());
        Assert.Equal(before, await _factory.PurchaseRowsAsync());
    }

    /// <summary>
    /// The identity the repair added, over the wire: a line id submitted in the items JSON keeps
    /// that line's own classification when the edit omits the GST field entirely.
    /// </summary>
    [Fact]
    public async Task A_submitted_line_id_keeps_that_lines_classification_on_an_edit()
    {
        var purchaseId = await CreatePurchaseAsync(gstClassification: "2");
        var productId = await _factory.ProductIdAsync(E2ETestFixture.PurchaseProductName);
        var lineId = await FirstLineIdAsync(purchaseId);

        var response = await _factory.BusinessA().PutAsync($"{PurchasesUri}/{purchaseId}", Form(
            items: Items(productId, gstClassification: null, id: lineId.ToString(), quantity: "7"),
            withFile: false));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var line = (await PurchaseAsync(response)).GetProperty("items").EnumerateArray().Single();
        Assert.Equal(lineId, line.GetProperty("id").GetInt32());
        Assert.Equal(7m, line.GetProperty("quantity").GetDecimal());
        Assert.Equal((int)GstClassification.GstFree, line.GetProperty("gstClassification").GetInt32());
        Assert.Equal((int)GstClassificationSource.Manual, line.GetProperty("gstClassificationSource").GetInt32());
    }

    /// <summary>
    /// The purchase input-GST summary issue #431 added to the response envelope, over the real
    /// pipeline and on both the write and the read. The purchase form displays these figures; it
    /// never calculates them, so the response is where they have to come from.
    ///
    /// The amounts pin the component-level rounding: the 2.20 taxable line contributes 0.20 and the
    /// 5.00 taxable delivery charge contributes 0.45, while the unclassified 2.00 package charge
    /// contributes no GST at all and is reported separately as one unresolved component.
    /// </summary>
    [Fact]
    public async Task The_response_envelope_carries_the_saved_purchase_input_gst_summary()
    {
        var productId = await _factory.ProductIdAsync(E2ETestFixture.PurchaseProductName);

        var created = await _factory.BusinessA().PostAsync(PurchasesUri, Form(
            items: Items(productId, gstClassification: "1"),
            deliveryCost: "5.00",
            deliveryGstClassification: "1",
            packageCost: "2.00"));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        AssertSummary(await GstSummaryAsync(created), inputGst: 0.65m, unresolvedCount: 1, unresolvedAmount: 2.00m);

        var purchaseId = (await PurchaseAsync(created)).GetProperty("id").GetInt32();
        var read = await _factory.BusinessA().GetAsync($"{PurchasesUri}/{purchaseId}");

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        AssertSummary(await GstSummaryAsync(read), inputGst: 0.65m, unresolvedCount: 1, unresolvedAmount: 2.00m);
    }

    /// <summary>
    /// A delivery or package charge that was never entered has no classification and is never an
    /// unresolved component (parent issue #62, decision D3), so a purchase whose only component is a
    /// classified line reports nothing unresolved. Without this, the form would warn about charges
    /// the person deliberately left empty.
    /// </summary>
    [Fact]
    public async Task An_absent_charge_is_not_an_unresolved_component_of_the_summary()
    {
        var productId = await _factory.ProductIdAsync(E2ETestFixture.PurchaseProductName);

        var response = await _factory.BusinessA().PostAsync(PurchasesUri, Form(
            items: Items(productId, gstClassification: "2")));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        AssertSummary(await GstSummaryAsync(response), inputGst: 0m, unresolvedCount: 0, unresolvedAmount: 0m);
    }

    private static void AssertSummary(
        JsonElement summary, decimal inputGst, int unresolvedCount, decimal unresolvedAmount)
    {
        Assert.Equal(inputGst, summary.GetProperty("inputGst").GetDecimal());
        Assert.Equal(unresolvedCount, summary.GetProperty("unresolvedComponentCount").GetInt32());
        Assert.Equal(unresolvedAmount, summary.GetProperty("unresolvedAmount").GetDecimal());
    }

    private async Task<int> CreatePurchaseAsync(
        string? gstClassification, HttpClient? client = null, long? productId = null)
    {
        var resolvedProductId = productId ?? await _factory.ProductIdAsync(E2ETestFixture.PurchaseProductName);
        var response = await (client ?? _factory.BusinessA()).PostAsync(
            PurchasesUri, Form(items: Items(resolvedProductId, gstClassification)));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await PurchaseAsync(response)).GetProperty("id").GetInt32();
    }

    private async Task<int> FirstLineIdAsync(int purchaseId)
    {
        var response = await _factory.BusinessA().GetAsync($"{PurchasesUri}/{purchaseId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await PurchaseAsync(response)).GetProperty("items").EnumerateArray()
            .Single().GetProperty("id").GetInt32();
    }

    /// <summary>The <c>purchase</c> member of the endpoint's response envelope.</summary>
    private static async Task<JsonElement> PurchaseAsync(HttpResponseMessage response) =>
        await EnvelopeMemberAsync(response, "purchase");

    /// <summary>The <c>gst</c> member of the endpoint's response envelope (issue #431).</summary>
    private static async Task<JsonElement> GstSummaryAsync(HttpResponseMessage response) =>
        await EnvelopeMemberAsync(response, "gst");

    private static async Task<JsonElement> EnvelopeMemberAsync(HttpResponseMessage response, string name)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty(name).Clone();
    }

    /// <summary>
    /// One purchase line as the <c>items</c> form field carries it: a JSON string inside a
    /// multipart body, which is exactly the hand-written deserialization this test exercises.
    /// </summary>
    private static string Items(long productId, string? gstClassification, string? id = null, string quantity = "2")
    {
        var fields = new List<string>
        {
            $"\"productId\":{productId}",
            $"\"quantity\":{quantity}",
            "\"unitCost\":1.10",
        };
        if (gstClassification is not null) fields.Add($"\"gstClassification\":{gstClassification}");
        if (id is not null) fields.Add($"\"id\":{id}");
        return $"[{{{string.Join(",", fields)}}}]";
    }

    private static MultipartFormDataContent Form(
        string items,
        string? deliveryCost = null,
        string? deliveryGstClassification = null,
        string? packageCost = null,
        string? packageGstClassification = null,
        bool withFile = true)
    {
        var content = new MultipartFormDataContent();
        if (withFile)
        {
            var file = new ByteArrayContent([1, 2, 3]);
            file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            content.Add(file, "file", "scan.jpg");
        }

        content.Add(new StringContent("Weekly restock"), "title");
        content.Add(new StringContent("2026-04-01T10:00:00Z"), "purchaseDate");
        content.Add(new StringContent(items), "items");
        Add(deliveryCost, "deliveryCost");
        Add(deliveryGstClassification, "deliveryGstClassification");
        Add(packageCost, "packageCost");
        Add(packageGstClassification, "packageGstClassification");
        return content;

        void Add(string? value, string name)
        {
            if (value is not null) content.Add(new StringContent(value), name);
        }
    }

    /// <summary>
    /// The API hosted the way the end-to-end harness hosts it - the dedicated E2E environment, its
    /// two synthetic businesses and their memberships, and a disposable relational database - with
    /// document storage replaced, so an upload writes no file anywhere on this machine and a
    /// refused upload can be shown to have written none either.
    /// </summary>
    public sealed class ApiFactory : WebApplicationFactory<Program>
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        private readonly Mock<IDocumentStorage> _documents = new();

        public ApiFactory()
        {
            // Hosting must not depend on the process's current directory; see E2ETestHostContentRoot.
            E2ETestHostContentRoot.Pin();
            _documents
                .Setup(d => d.SaveAsync(
                    It.IsAny<DocumentCategory>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            _connection.Open();
        }

        public HttpClient BusinessA() => As(E2ETestActors.BusinessAOwner.Key);

        public HttpClient BusinessB() => As(E2ETestActors.BusinessBOwner.Key);

        public async Task<long> ProductIdAsync(string productName)
        {
            await using var db = Database();
            return (await db.Products.AsNoTracking().SingleAsync(product => product.Name == productName)).Id;
        }

        /// <summary>
        /// Every persisted purchase and purchase line, as one comparable value, so a refused
        /// request can be shown to have changed no row of either business.
        /// </summary>
        public async Task<string> PurchaseRowsAsync()
        {
            await using var db = Database();
            var purchases = await db.Receipts.AsNoTracking().OrderBy(r => r.Id)
                .Select(r => $"{r.Id}:{r.BusinessId}:{r.Title}:{r.DeliveryCost}:{r.DeliveryGstClassification}:" +
                    $"{r.DeliveryGstClassificationSource}:{r.PackageCost}:{r.PackageGstClassification}:" +
                    $"{r.PackageGstClassificationSource}")
                .ToListAsync();
            var items = await db.ReceiptItems.AsNoTracking().OrderBy(i => i.Id)
                .Select(i => $"{i.Id}:{i.BusinessId}:{i.ReceiptId}:{i.ProductId}:{i.Quantity}:{i.UnitCost}:" +
                    $"{i.GstClassification}:{i.GstClassificationSource}")
                .ToListAsync();
            return string.Join("|", purchases.Concat(items));
        }

        /// <summary>
        /// How many documents this host has stored so far. Compared across one request rather than
        /// asserted to be zero, because the host - and so the recording double - is shared by every
        /// test in the class, and the accepted uploads legitimately store one each.
        /// </summary>
        public int DocumentSaveCount => _documents.Invocations
            .Count(invocation => invocation.Method.Name == nameof(IDocumentStorage.SaveAsync));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(E2ETestEnvironment.EnvironmentName);

            // The E2E environment is not one of the environments that migrate automatically, so
            // this disposable database opts in with the setting that exists for exactly that case.
            builder.UseSetting("Database:AllowAutomaticMigrationUnsafeOutsideDevelopment", "true");

            builder.ConfigureServices(services =>
            {
                var dbContextOptions = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                if (dbContextOptions is not null)
                {
                    services.Remove(dbContextOptions);
                }

                services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));
            });

            // Registered last, so the uploaded scan never reaches the filesystem adapter.
            builder.ConfigureTestServices(services => services.AddSingleton(_documents.Object));
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                _connection.Dispose();
            }
        }

        private HttpClient As(string actorKey)
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.TryAddWithoutValidation(E2ETestActors.ActorHeaderName, actorKey);
            return client;
        }

        /// <summary>
        /// An unrestricted context for arranging and verifying both businesses' rows from outside
        /// the boundary, never over HTTP. Resolving <c>Services</c> starts the host, which is what
        /// creates the schema and seeds the fixture this then reads.
        /// </summary>
        private AppDbContext Database()
        {
            _ = Services;
            return TestAppDbContext.Unrestricted(
                new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        }
    }
}
