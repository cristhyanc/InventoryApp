using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Inventory.Application.Documents;
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
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// A purchase may be recorded with no supporting document at all, and a document may be added to
/// one later (issue #976, child of #965). These run over the real HTTP pipeline - a genuine
/// <c>multipart/form-data</c> body, the framework's own binding, authentication, the business-scope
/// middleware and relational SQLite - because what is being proved is the request contract: a body
/// that carries no <c>file</c> part at all, which no directly constructed controller call can show.
/// </summary>
public sealed class PurchaseWithoutDocumentApiTests : IClassFixture<PurchaseWithoutDocumentApiTests.ApiFactory>
{
    private const string PurchasesUri = "/api/purchases";

    private readonly ApiFactory _factory;

    public PurchaseWithoutDocumentApiTests(ApiFactory factory) => _factory = factory;

    /// <summary>
    /// The business outcome of #965: a business with no receipt to attach - or no storage left to
    /// put one in - can still record what it bought. The purchase's own fields are saved exactly as
    /// they are with a document, and its document metadata comes back absent rather than blank.
    /// </summary>
    [Fact]
    public async Task A_purchase_is_created_with_no_document_and_reports_none()
    {
        var productId = await _factory.ProductIdAsync(E2ETestFixture.PurchaseProductName);

        var response = await _factory.BusinessA().PostAsync(PurchasesUri, Form(items: Items(productId), withFile: false));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var purchase = await PurchaseAsync(response);
        Assert.Equal("Weekly restock", purchase.GetProperty("title").GetString());
        Assert.Equal(JsonValueKind.Null, purchase.GetProperty("fileName").ValueKind);
        Assert.Equal(JsonValueKind.Null, purchase.GetProperty("storedFileName").ValueKind);
        Assert.Equal(JsonValueKind.Null, purchase.GetProperty("contentType").ValueKind);
        Assert.Equal(JsonValueKind.Null, purchase.GetProperty("fileSizeBytes").ValueKind);
    }

    /// <summary>
    /// The control that keeps the test above about the <em>absent</em> document rather than about an
    /// endpoint that stopped storing documents: with a file part, the four values come back and the
    /// bytes really are in storage.
    /// </summary>
    [Fact]
    public async Task A_purchase_with_a_document_is_unchanged()
    {
        var productId = await _factory.ProductIdAsync(E2ETestFixture.PurchaseProductName);

        var response = await _factory.BusinessA().PostAsync(PurchasesUri, Form(items: Items(productId), withFile: true));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var purchase = await PurchaseAsync(response);
        Assert.Equal("scan.jpg", purchase.GetProperty("fileName").GetString());
        Assert.Equal("image/jpeg", purchase.GetProperty("contentType").GetString());
        Assert.Equal(3, purchase.GetProperty("fileSizeBytes").GetInt64());
        Assert.True(_factory.Documents.Contains(
            DocumentCategory.PurchaseDocument, purchase.GetProperty("storedFileName").GetString()!));
    }

    /// <summary>
    /// The document rules are the ones that were already there: an empty part and a type that is not
    /// an image or a PDF are still refused on create, with the same deliberately vague message, and
    /// store nothing. "No file at all" is the only thing that changed.
    /// </summary>
    [Theory]
    [InlineData("notes.txt", 3)]
    [InlineData("scan", 3)]
    [InlineData("scan.jpg", 0)]
    public async Task An_unacceptable_document_is_still_refused_on_create(string fileName, int byteCount)
    {
        var productId = await _factory.ProductIdAsync(E2ETestFixture.PurchaseProductName);
        var savedBefore = _factory.Documents.SavedFileNames.Count;
        var purchasesBefore = await _factory.PurchaseCountAsync();

        var response = await _factory.BusinessA().PostAsync(
            PurchasesUri, Form(items: Items(productId), withFile: true, fileName: fileName, byteCount: byteCount));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Invalid file or supplier", await response.Content.ReadAsStringAsync());
        Assert.Equal(savedBefore, _factory.Documents.SavedFileNames.Count);
        Assert.Equal(purchasesBefore, await _factory.PurchaseCountAsync());
    }

    /// <summary>
    /// With a document the title falls back to the uploaded file's name; with none there is nothing
    /// to fall back to, so a purchase with no document has to be named - otherwise it would be a row
    /// nobody can recognise in a list.
    ///
    /// <para>Over HTTP the framework answers this first: <c>title</c> is a non-nullable parameter, so
    /// MVC's implicit required validation refuses a blank one with a <c>ProblemDetails</c> before the
    /// action runs - which is why this asserts the refusal and the absence of a purchase rather than
    /// a message. The Application layer refuses it too, by name, and that is
    /// <c>UploadPurchaseTests.A_blank_title_with_no_document_is_refused</c>: a use case must not
    /// depend on a transport's validation to keep an invariant.</para>
    /// </summary>
    [Theory]
    [InlineData("   ")]
    [InlineData("")]
    public async Task A_purchase_with_no_document_still_needs_a_title(string title)
    {
        var productId = await _factory.ProductIdAsync(E2ETestFixture.PurchaseProductName);
        var purchasesBefore = await _factory.PurchaseCountAsync();

        var response = await _factory.BusinessA().PostAsync(
            PurchasesUri, Form(items: Items(productId), withFile: false, title: title));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(purchasesBefore, await _factory.PurchaseCountAsync());
    }

    /// <summary>
    /// A purchase with no document has no document to download, and that is a <c>404</c> - the same
    /// answer a purchase that does not exist gets, and the same answer a purchase whose stored bytes
    /// have gone missing already got. The lists' "No document" is the frontend half of this.
    /// </summary>
    [Fact]
    public async Task The_file_endpoint_answers_not_found_for_a_purchase_with_no_document()
    {
        var purchaseId = await CreateAsync(withFile: false);

        var response = await _factory.BusinessA().GetAsync($"{PurchasesUri}/{purchaseId}/file");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// The second half of the business outcome: the receipt turns up later, or storage frees up, and
    /// the document is added to the purchase that was already recorded. The purchase's own fields are
    /// untouched, and the document is then downloadable under its original name and content type.
    /// </summary>
    [Fact]
    public async Task A_document_can_be_added_later_and_is_then_downloadable()
    {
        var purchaseId = await CreateAsync(withFile: false);

        var attached = await _factory.BusinessA().PostAsync($"{PurchasesUri}/{purchaseId}/file", Document());

        Assert.Equal(HttpStatusCode.OK, attached.StatusCode);
        var purchase = await PurchaseAsync(attached);
        Assert.Equal(purchaseId, purchase.GetProperty("id").GetInt32());
        Assert.Equal("Weekly restock", purchase.GetProperty("title").GetString());
        Assert.Equal("scan.jpg", purchase.GetProperty("fileName").GetString());
        Assert.Equal("image/jpeg", purchase.GetProperty("contentType").GetString());
        Assert.Equal(3, purchase.GetProperty("fileSizeBytes").GetInt64());
        var storedFileName = purchase.GetProperty("storedFileName").GetString()!;
        Assert.True(_factory.Documents.Contains(DocumentCategory.PurchaseDocument, storedFileName));

        var download = await _factory.BusinessA().GetAsync($"{PurchasesUri}/{purchaseId}/file");

        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("image/jpeg", download.Content.Headers.ContentType?.MediaType);
        Assert.Equal(new byte[] { 1, 2, 3 }, await download.Content.ReadAsByteArrayAsync());
    }

    /// <summary>
    /// Attach only if empty. Replacing a document is out of scope, so a purchase that already has one
    /// is a <c>409</c> - and the loser's bytes are removed rather than left in storage with nothing
    /// naming them. This is the sequential contract; the real race is
    /// <c>PurchaseDocumentAttachConcurrencyTests</c>.
    /// </summary>
    [Fact]
    public async Task A_second_attach_is_refused_as_a_conflict_and_removes_its_own_bytes()
    {
        var purchaseId = await CreateAsync(withFile: false);
        var first = await _factory.BusinessA().PostAsync($"{PurchasesUri}/{purchaseId}/file", Document());
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var storedFileName = (await PurchaseAsync(first)).GetProperty("storedFileName").GetString()!;
        var savedBefore = _factory.Documents.SavedFileNames.Count;

        var second = await _factory.BusinessA().PostAsync(
            $"{PurchasesUri}/{purchaseId}/file", Document(fileName: "replacement.jpg"));

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(PurchaseDocumentPolicy.AlreadyHasDocumentMessage, await BodyAsync(second));

        // The purchase still has exactly the first document, and the refused upload's bytes are gone.
        var purchase = await ReadPurchaseAsync(purchaseId);
        Assert.Equal("scan.jpg", purchase.GetProperty("fileName").GetString());
        Assert.Equal(storedFileName, purchase.GetProperty("storedFileName").GetString());
        Assert.True(_factory.Documents.Contains(DocumentCategory.PurchaseDocument, storedFileName));
        var refusedStoredName = _factory.Documents.SavedFileNames.Skip(savedBefore).Single();
        Assert.Contains(refusedStoredName, _factory.Documents.DeletedFileNames);
        Assert.False(_factory.Documents.Contains(DocumentCategory.PurchaseDocument, refusedStoredName));
    }

    /// <summary>
    /// A document the rules refuse records nothing and stores nothing: the refusal happens before any
    /// bytes are written, so there is nothing to clean up and the purchase still has no document.
    /// </summary>
    [Theory]
    [InlineData("notes.txt", 3, PurchaseDocumentPolicy.ExtensionErrorMessage)]
    [InlineData("scan.jpg", 0, PurchaseDocumentPolicy.SizeErrorMessage)]
    public async Task An_unacceptable_document_on_attach_records_and_stores_nothing(
        string fileName, int byteCount, string message)
    {
        var purchaseId = await CreateAsync(withFile: false);
        var savedBefore = _factory.Documents.SavedFileNames.Count;

        var response = await _factory.BusinessA().PostAsync(
            $"{PurchasesUri}/{purchaseId}/file", Document(fileName, byteCount));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(message, await BodyAsync(response));
        Assert.Equal(savedBefore, _factory.Documents.SavedFileNames.Count);
        Assert.Equal(
            JsonValueKind.Null, (await ReadPurchaseAsync(purchaseId)).GetProperty("storedFileName").ValueKind);
    }

    /// <summary>
    /// An attach naming a purchase of the <em>other</em> business is a <c>404</c> - the tenant query
    /// filters mean it is simply not there - and it changes nothing on either side. The bytes the
    /// request had already written are removed, so a caller cannot leave a document in another
    /// business's storage by guessing a purchase id.
    /// </summary>
    [Fact]
    public async Task An_attach_naming_another_businesss_purchase_is_not_found_and_changes_nothing()
    {
        var businessAPurchaseId = await CreateAsync(withFile: false);
        var savedBefore = _factory.Documents.SavedFileNames.Count;

        var response = await _factory.BusinessB().PostAsync(
            $"{PurchasesUri}/{businessAPurchaseId}/file", Document());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(
            JsonValueKind.Null, (await ReadPurchaseAsync(businessAPurchaseId)).GetProperty("storedFileName").ValueKind);
        var refusedStoredName = _factory.Documents.SavedFileNames.Skip(savedBefore).Single();
        Assert.Contains(refusedStoredName, _factory.Documents.DeletedFileNames);
        Assert.False(_factory.Documents.Contains(DocumentCategory.PurchaseDocument, refusedStoredName));
    }

    /// <summary>A purchase that does not exist at all is the same <c>404</c>, and leaves no bytes behind either.</summary>
    [Fact]
    public async Task An_attach_to_an_unknown_purchase_is_not_found_and_removes_its_bytes()
    {
        var savedBefore = _factory.Documents.SavedFileNames.Count;

        var response = await _factory.BusinessA().PostAsync($"{PurchasesUri}/999999/file", Document());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var refusedStoredName = _factory.Documents.SavedFileNames.Skip(savedBefore).Single();
        Assert.False(_factory.Documents.Contains(DocumentCategory.PurchaseDocument, refusedStoredName));
    }

    /// <summary>
    /// The invariant the issue puts first: a document is paperwork, and paperwork is not money. Two
    /// identical purchases, one with a document and one without, move the product's physical stock,
    /// its costing quantity, its inventory value and its weighted-average cost by exactly the same
    /// amounts, record the same restock movement, and report the same input-GST block - so no
    /// stock, cost or GST figure depends on whether a receipt was attached.
    /// </summary>
    [Fact]
    public async Task Stock_cost_and_gst_results_are_identical_with_and_without_a_document()
    {
        var productId = await _factory.ProductIdAsync(E2ETestFixture.PurchaseProductName);

        var before = await _factory.ProductCostingAsync(productId);
        var documented = await _factory.BusinessA().PostAsync(PurchasesUri, GstForm(productId, withFile: true));
        Assert.Equal(HttpStatusCode.Created, documented.StatusCode);
        var afterDocumented = await _factory.ProductCostingAsync(productId);

        var undocumented = await _factory.BusinessA().PostAsync(PurchasesUri, GstForm(productId, withFile: false));
        Assert.Equal(HttpStatusCode.Created, undocumented.StatusCode);
        var afterUndocumented = await _factory.ProductCostingAsync(productId);

        // The same movement in stock, costing quantity and inventory value, both times.
        Assert.Equal(
            (afterDocumented.QuantityInStock - before.QuantityInStock,
                afterDocumented.CostingQuantity - before.CostingQuantity,
                afterDocumented.InventoryValue - before.InventoryValue),
            (afterUndocumented.QuantityInStock - afterDocumented.QuantityInStock,
                afterUndocumented.CostingQuantity - afterDocumented.CostingQuantity,
                afterUndocumented.InventoryValue - afterDocumented.InventoryValue));

        // Both purchases were at the same unit cost, so the weighted average is where it started.
        Assert.Equal(before.AverageUnitCost, afterUndocumented.AverageUnitCost);

        // The restock movement each purchase recorded, compared on everything but its own identity.
        Assert.Equal(
            await _factory.RestockMovementAsync((await PurchaseAsync(documented)).GetProperty("id").GetInt32()),
            await _factory.RestockMovementAsync((await PurchaseAsync(undocumented)).GetProperty("id").GetInt32()));

        // And the server's input-GST block: the same amount, the same unresolved count and amount.
        Assert.Equal(await GstSummaryAsync(documented), await GstSummaryAsync(undocumented));
    }

    private async Task<int> CreateAsync(bool withFile)
    {
        var productId = await _factory.ProductIdAsync(E2ETestFixture.PurchaseProductName);
        var response = await _factory.BusinessA().PostAsync(PurchasesUri, Form(items: Items(productId), withFile));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await PurchaseAsync(response)).GetProperty("id").GetInt32();
    }

    private async Task<JsonElement> ReadPurchaseAsync(int purchaseId)
    {
        var response = await _factory.BusinessA().GetAsync($"{PurchasesUri}/{purchaseId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await PurchaseAsync(response);
    }

    /// <summary>The <c>purchase</c> member of the endpoint's response envelope.</summary>
    private static async Task<JsonElement> PurchaseAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("purchase").Clone();
    }

    /// <summary>The <c>gst</c> member of the envelope, as a string so two responses compare whole.</summary>
    private static async Task<string> GstSummaryAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("gst").GetRawText();
    }

    private static Task<string> BodyAsync(HttpResponseMessage response) =>
        response.Content.ReadAsStringAsync();

    private static string Items(long productId, string quantity = "2") =>
        $"[{{\"productId\":{productId},\"quantity\":{quantity},\"unitCost\":1.10}}]";

    private static MultipartFormDataContent Form(
        string items,
        bool withFile,
        string title = "Weekly restock",
        string fileName = "scan.jpg",
        int byteCount = 3)
    {
        var content = new MultipartFormDataContent();
        if (withFile) content.Add(FilePart(fileName, byteCount), "file", fileName);

        content.Add(new StringContent(title), "title");
        content.Add(new StringContent("2026-04-01T10:00:00Z"), "purchaseDate");
        content.Add(new StringContent(items), "items");
        return content;
    }

    /// <summary>
    /// The same purchase twice over, with and without a document: one classified line, a classified
    /// delivery charge and an unclassified package charge, so the input-GST block being compared has
    /// a resolved amount <em>and</em> an unresolved component in it.
    /// </summary>
    private static MultipartFormDataContent GstForm(long productId, bool withFile)
    {
        var content = Form(
            items: $"[{{\"productId\":{productId},\"quantity\":4,\"unitCost\":1.10,\"gstClassification\":1}}]",
            withFile);
        content.Add(new StringContent("5.00"), "deliveryCost");
        content.Add(new StringContent("1"), "deliveryGstClassification");
        content.Add(new StringContent("2.00"), "packageCost");
        return content;
    }

    /// <summary>A body carrying one document part, as the attach endpoint accepts it.</summary>
    private static MultipartFormDataContent Document(string fileName = "scan.jpg", int byteCount = 3)
    {
        var content = new MultipartFormDataContent();
        content.Add(FilePart(fileName, byteCount), "file", fileName);
        return content;
    }

    private static ByteArrayContent FilePart(string fileName, int byteCount)
    {
        var part = new ByteArrayContent(Enumerable.Range(1, byteCount).Select(value => (byte)value).ToArray());
        part.Headers.ContentType = new MediaTypeHeaderValue(
            fileName.EndsWith(".txt", StringComparison.Ordinal) ? "text/plain" : "image/jpeg");
        return part;
    }

    /// <summary>
    /// The API hosted the way the end-to-end harness hosts it, with document storage replaced by an
    /// in-memory double so an upload writes no file anywhere on this machine and a failed attach can
    /// be shown to have removed the bytes it had already written.
    /// </summary>
    public sealed class ApiFactory : WebApplicationFactory<Program>
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");

        public ApiFactory()
        {
            // Hosting must not depend on the process's current directory; see E2ETestHostContentRoot.
            E2ETestHostContentRoot.Pin();
            _connection.Open();
        }

        public InMemoryDocumentStorage Documents { get; } = new();

        public HttpClient BusinessA() => As(E2ETestActors.BusinessAOwner.Key);

        public HttpClient BusinessB() => As(E2ETestActors.BusinessBOwner.Key);

        public async Task<long> ProductIdAsync(string productName)
        {
            await using var db = Database();
            return (await db.Products.AsNoTracking().SingleAsync(product => product.Name == productName)).Id;
        }

        /// <summary>
        /// How many purchases both businesses hold, so a refused request can be shown to have
        /// recorded none. Compared across one request rather than asserted to be zero, because the
        /// host is shared by every test in the class.
        /// </summary>
        public async Task<int> PurchaseCountAsync()
        {
            await using var db = Database();
            return await db.Receipts.AsNoTracking().CountAsync();
        }

        /// <summary>
        /// The product's physical stock and its whole costing position, read from the database rather
        /// than from a response, so "a document changes no figure" is a claim about what was stored.
        /// </summary>
        public async Task<ProductCosting> ProductCostingAsync(long productId)
        {
            await using var db = Database();
            return await db.Products.AsNoTracking()
                .Where(product => product.Id == productId)
                .Select(product => new ProductCosting(
                    product.QuantityInStock,
                    product.CostingQuantity,
                    product.InventoryValue,
                    product.AverageUnitCost))
                .SingleAsync();
        }

        /// <summary>
        /// The restock movement one purchase recorded, as one comparable value with nothing
        /// purchase-specific in it: the product, the reason, the quantity, the unit cost, the total
        /// cost, the weighted average it left and its notes.
        ///
        /// <para>The movement's <c>CostingQuantityAfter</c> and <c>InventoryValueAfter</c> are
        /// deliberately left out: they are the running position after that movement, so the second
        /// of two purchases of the same product legitimately records a higher one. That the position
        /// <em>moved</em> by the same amount is asserted separately, from the product's own row.</para>
        /// </summary>
        public async Task<string> RestockMovementAsync(int purchaseId)
        {
            await using var db = Database();
            var itemIds = await db.ReceiptItems.AsNoTracking()
                .Where(item => item.ReceiptId == purchaseId)
                .Select(item => item.Id)
                .ToListAsync();
            var movements = await db.StockAdjustments.AsNoTracking()
                .Where(movement => movement.ReceiptItemId.HasValue && itemIds.Contains(movement.ReceiptItemId.Value))
                .OrderBy(movement => movement.ProductId)
                .Select(movement => $"{movement.ProductId}:{movement.Reason}:{movement.QuantityChange}:" +
                    $"{movement.UnitCost}:{movement.TotalCost}:{movement.AverageUnitCostAfter}:{movement.Notes}")
                .ToListAsync();
            return string.Join("|", movements);
        }

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

            // Registered last, so an uploaded scan never reaches the filesystem adapter.
            builder.ConfigureTestServices(services => services.AddSingleton<IDocumentStorage>(Documents));
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

    /// <summary>One product's physical stock and its costing position, as the database holds them.</summary>
    public sealed record ProductCosting(
        int QuantityInStock, int? CostingQuantity, decimal? InventoryValue, decimal AverageUnitCost);

    /// <summary>
    /// An in-memory <see cref="IDocumentStorage"/> that records what it holds, so a test can assert
    /// both halves of an attach: the bytes that were kept, and the bytes a refusal removed again.
    /// </summary>
    public sealed class InMemoryDocumentStorage : IDocumentStorage
    {
        private readonly Dictionary<(DocumentCategory Category, string StoredFileName), byte[]> _documents = [];

        public List<string> SavedFileNames { get; } = [];

        public List<string?> DeletedFileNames { get; } = [];

        /// <summary>When set, the next <see cref="SaveAsync"/> throws instead of storing anything.</summary>
        public bool ThrowOnSave { get; set; }

        public async Task SaveAsync(
            DocumentCategory category, string storedFileName, Stream content, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(content);
            if (ThrowOnSave) throw new InvalidOperationException("storage failure");

            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            lock (_documents)
            {
                _documents[(category, storedFileName)] = buffer.ToArray();
                SavedFileNames.Add(storedFileName);
            }
        }

        public Task<DocumentContent?> OpenReadAsync(
            DocumentCategory category, string? storedFileName, CancellationToken cancellationToken = default)
        {
            lock (_documents)
            {
                if (storedFileName is null || !_documents.TryGetValue((category, storedFileName), out var bytes))
                    return Task.FromResult<DocumentContent?>(null);

                return Task.FromResult<DocumentContent?>(new DocumentContent(new MemoryStream(bytes), bytes.Length));
            }
        }

        public Task<bool> DeleteAsync(
            DocumentCategory category, string? storedFileName, CancellationToken cancellationToken = default)
        {
            lock (_documents)
            {
                DeletedFileNames.Add(storedFileName);
                return Task.FromResult(storedFileName is not null && _documents.Remove((category, storedFileName)));
            }
        }

        public bool Contains(DocumentCategory category, string storedFileName)
        {
            lock (_documents)
            {
                return _documents.ContainsKey((category, storedFileName));
            }
        }
    }
}
