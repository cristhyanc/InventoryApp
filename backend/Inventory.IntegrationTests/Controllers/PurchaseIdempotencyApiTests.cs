using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Inventory.Application.Documents;
using Inventory.Domain.Purchases;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
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
/// A network retry or a double press cannot save one purchase twice (issue #1247), and the duplicate
/// warning that sits beside it.
///
/// <para>These run over the real HTTP pipeline - a genuine <c>multipart/form-data</c> body, the
/// framework's own binding, authentication, the business-scope middleware and relational SQLite -
/// because what is being proved is the request/response contract, what reaches the database and the
/// tenant boundary, none of which a directly constructed controller call shows.</para>
/// </summary>
public sealed class PurchaseIdempotencyApiTests : IClassFixture<PurchaseIdempotencyApiTests.ApiFactory>
{
    private const string PurchasesUri = "/api/purchases";

    private readonly ApiFactory _factory;

    public PurchaseIdempotencyApiTests(ApiFactory factory) => _factory = factory;

    /// <summary>
    /// The issue's first criterion: the same key with the same content saves one purchase, answers
    /// with it both times, and records one set of stock movements - so the product's stock and
    /// inventory value move once.
    /// </summary>
    [Fact]
    public async Task The_same_request_sent_twice_under_one_key_saves_one_purchase()
    {
        var key = NewKey();
        var productId = await _factory.ProductIdAsync();
        var stockBefore = await _factory.ProductStockAsync(productId);
        var movementsBefore = await _factory.StockMovementCountAsync();

        var first = await _factory.BusinessA().PostAsync(PurchasesUri, await FormAsync(key));
        var retry = await _factory.BusinessA().PostAsync(PurchasesUri, await FormAsync(key));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        var saved = await PurchaseAsync(first);
        var replayed = await PurchaseAsync(retry);
        var purchaseId = saved.GetProperty("id").GetInt32();
        Assert.Equal(purchaseId, replayed.GetProperty("id").GetInt32());

        // The whole purchase, not merely its id, and from all three paths: the create, the retry
        // that replays it and a read of it serialize one purchase identically.
        Assert.Equal(saved.GetRawText(), replayed.GetRawText());
        Assert.Equal((await ReadAsync(purchaseId)).GetRawText(), replayed.GetRawText());

        // One purchase, one restock movement, and the two units on the receipt added to stock once.
        Assert.Equal(1, await _factory.PurchaseCountForKeyAsync(key));
        Assert.Equal(1, await _factory.RestockMovementCountAsync(purchaseId));
        Assert.Equal(movementsBefore + 1, await _factory.StockMovementCountAsync());
        Assert.Equal(stockBefore + 2, await _factory.ProductStockAsync(productId));
    }

    /// <summary>
    /// The issue's second criterion, and the repository owner's decision of 14 November 2026: the
    /// same key with different details is refused with the sentence that asks for a reload, and
    /// nothing at all is written.
    /// </summary>
    [Fact]
    public async Task The_same_key_with_different_details_is_refused_and_writes_nothing()
    {
        var key = NewKey();
        await Created(key);
        var purchasesBefore = await _factory.PurchaseCountAsync();
        var movementsBefore = await _factory.StockMovementCountAsync();

        var response = await _factory.BusinessA().PostAsync(
            PurchasesUri, await FormAsync(key, invoiceNumber: "INV-DIFFERENT"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            PurchaseIdempotencyPolicy.KeyReusedMessage,
            problem.RootElement.GetProperty("detail").GetString());
        Assert.Equal(purchasesBefore, await _factory.PurchaseCountAsync());
        Assert.Equal(movementsBefore, await _factory.StockMovementCountAsync());
    }

    /// <summary>
    /// A <em>different</em> receipt under a spent key is refused even when it is indistinguishable
    /// from the first one by name and size - two photographs a phone named the same way, both three
    /// bytes here. The document's name and length cannot decide this: only its bytes can, which is
    /// why the fingerprint renders their digest. Being replayed instead would answer the operator
    /// with the first receipt's purchase and silently lose the second one.
    /// </summary>
    [Fact]
    public async Task The_same_key_with_a_different_document_of_the_same_name_and_length_is_refused()
    {
        var key = NewKey();
        var created = await _factory.BusinessA().PostAsync(PurchasesUri, await FormAsync(key, document: [1, 2, 3]));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var purchase = await PurchaseAsync(created);
        var storedFileName = purchase.GetProperty("storedFileName").GetString()!;
        Assert.True(_factory.Documents.Contains(DocumentCategory.PurchaseDocument, storedFileName));
        var purchasesBefore = await _factory.PurchaseCountAsync();
        var movementsBefore = await _factory.StockMovementCountAsync();
        var documentsBefore = _factory.Documents.SavedFileNames.Count;

        var refused = await _factory.BusinessA().PostAsync(PurchasesUri, await FormAsync(key, document: [3, 2, 1]));

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        using var problem = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
        Assert.Equal(
            PurchaseIdempotencyPolicy.KeyReusedMessage,
            problem.RootElement.GetProperty("detail").GetString());

        // No second purchase, no second restock movement and no second document: the refusal is
        // made before a byte of the second receipt is written anywhere.
        Assert.Equal(purchasesBefore, await _factory.PurchaseCountAsync());
        Assert.Equal(movementsBefore, await _factory.StockMovementCountAsync());
        Assert.Equal(documentsBefore, _factory.Documents.SavedFileNames.Count);

        // And the purchase that was saved still has the document it was saved with, bytes and all:
        // hashing the upload to fingerprint it must not consume the bytes storage writes.
        Assert.Equal(storedFileName, (await ReadAsync(purchase.GetProperty("id").GetInt32()))
            .GetProperty("storedFileName").GetString());
        Assert.True(_factory.Documents.Contains(DocumentCategory.PurchaseDocument, storedFileName));
        var download = await _factory.BusinessA().GetAsync(
            $"{PurchasesUri}/{purchase.GetProperty("id").GetInt32()}/file");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(new byte[] { 1, 2, 3 }, await download.Content.ReadAsByteArrayAsync());
    }

    /// <summary>
    /// The same receipt sent again under its key - the same bytes this time - is the retry the key
    /// exists for: one purchase, and the retry's own bytes are never stored at all.
    /// </summary>
    [Fact]
    public async Task A_retry_with_the_same_document_replays_and_stores_no_second_document()
    {
        var key = NewKey();
        var created = await _factory.BusinessA().PostAsync(PurchasesUri, await FormAsync(key, document: [1, 2, 3]));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var documentsBefore = _factory.Documents.SavedFileNames.Count;

        var retry = await _factory.BusinessA().PostAsync(PurchasesUri, await FormAsync(key, document: [1, 2, 3]));

        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal((await PurchaseAsync(created)).GetRawText(), (await PurchaseAsync(retry)).GetRawText());
        Assert.Equal(documentsBefore, _factory.Documents.SavedFileNames.Count);
        Assert.Equal(1, await _factory.PurchaseCountForKeyAsync(key));
    }

    /// <summary>
    /// The create and the replay are one projection, including on a purchase date that sits exactly
    /// on a business-day boundary - midnight in the business's own time zone, which is the instant a
    /// response that serialized the date differently from a read would move to the day before.
    /// </summary>
    [Fact]
    public async Task A_purchase_dated_on_a_business_day_boundary_is_serialized_the_same_way_by_both_paths()
    {
        var key = NewKey();

        var created = await _factory.BusinessA().PostAsync(PurchasesUri, await FormAsync(key, purchaseDate: BusinessDayStart));
        var retry = await _factory.BusinessA().PostAsync(PurchasesUri, await FormAsync(key, purchaseDate: BusinessDayStart));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        var saved = await PurchaseAsync(created);
        var replayed = await PurchaseAsync(retry);
        Assert.Equal(saved.GetRawText(), replayed.GetRawText());
        Assert.Equal((await ReadAsync(saved.GetProperty("id").GetInt32())).GetRawText(), saved.GetRawText());

        // The instant itself, so a projection that agreed with itself while shifting the day would
        // still be caught: 20 April 2026 in Sydney begins at 14:00 UTC on 19 April.
        Assert.StartsWith(
            "2026-04-19T14:00:00",
            saved.GetProperty("purchaseDate").GetString(),
            StringComparison.Ordinal);
    }

    /// <summary>Keys are tenant-owned: the same string in another business is another business's key.</summary>
    [Fact]
    public async Task The_same_key_in_another_business_saves_its_own_purchase()
    {
        var key = NewKey();

        var mine = await _factory.BusinessA().PostAsync(PurchasesUri, await FormAsync(key));
        var theirs = await _factory.BusinessB().PostAsync(PurchasesUri, await FormAsync(key, product: false));

        Assert.Equal(HttpStatusCode.Created, mine.StatusCode);
        Assert.Equal(HttpStatusCode.Created, theirs.StatusCode);
        Assert.NotEqual(
            (await PurchaseAsync(mine)).GetProperty("id").GetInt32(),
            (await PurchaseAsync(theirs)).GetProperty("id").GetInt32());
        Assert.Equal(2, await _factory.PurchaseCountForKeyAsync(key));
    }

    /// <summary>Backwards compatible: a create that sends no key is the create that always existed.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_create_without_a_key_behaves_as_before(string? key)
    {
        var first = await _factory.BusinessA().PostAsync(PurchasesUri, await FormAsync(key));
        var second = await _factory.BusinessA().PostAsync(PurchasesUri, await FormAsync(key));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var firstId = (await PurchaseAsync(first)).GetProperty("id").GetInt32();
        var secondId = (await PurchaseAsync(second)).GetProperty("id").GetInt32();
        Assert.NotEqual(firstId, secondId);
        Assert.Null(await _factory.StoredKeyAsync(firstId));
        Assert.Null(await _factory.StoredKeyAsync(secondId));
    }

    [Fact]
    public async Task An_over_long_key_is_refused_and_saves_nothing()
    {
        var purchasesBefore = await _factory.PurchaseCountAsync();

        var response = await _factory.BusinessA().PostAsync(
            PurchasesUri,
            await FormAsync(new string('k', PurchaseIdempotencyPolicy.MaximumKeyLength + 1)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(PurchaseIdempotencyPolicy.KeyTooLongMessage, await response.Content.ReadAsStringAsync());
        Assert.Equal(purchasesBefore, await _factory.PurchaseCountAsync());
    }

    /// <summary>
    /// The issue's sixth criterion, server half: a purchase with the same supplier, the same invoice
    /// number and the same date is reported, and the warning says which purchase it is.
    /// </summary>
    [Fact]
    public async Task A_purchase_with_the_same_supplier_invoice_number_and_date_is_reported()
    {
        var invoiceNumber = $"INV-DUP-{Guid.NewGuid():N}";
        var purchaseId = await Created(NewKey(), invoiceNumber: invoiceNumber, supplierId: _factory.SupplierId());

        var match = await DuplicateAsync(invoiceNumber, _factory.SupplierId(), PurchaseDate);

        Assert.Equal(JsonValueKind.Object, match.ValueKind);
        Assert.Equal(purchaseId, match.GetProperty("purchaseId").GetInt32());
        Assert.Equal("Weekly restock", match.GetProperty("title").GetString());
    }

    /// <summary>
    /// And the three ways it is not the same purchase. A different supplier, a different number or
    /// a different day is a different receipt, and warning about it would teach an operator to
    /// ignore the warning.
    /// </summary>
    [Fact]
    public async Task A_different_supplier_number_or_day_is_not_reported()
    {
        var invoiceNumber = $"INV-DUP-{Guid.NewGuid():N}";
        await Created(NewKey(), invoiceNumber: invoiceNumber, supplierId: _factory.SupplierId());

        Assert.Equal(JsonValueKind.Null, (await DuplicateAsync(invoiceNumber, _factory.OtherSupplierId(), PurchaseDate)).ValueKind);
        Assert.Equal(JsonValueKind.Null, (await DuplicateAsync($"{invoiceNumber}-X", _factory.SupplierId(), PurchaseDate)).ValueKind);
        Assert.Equal(
            JsonValueKind.Null,
            (await DuplicateAsync(invoiceNumber, _factory.SupplierId(), PurchaseDate.AddDays(1))).ValueKind);
    }

    /// <summary>
    /// A purchase with no supplier or no invoice number carries nothing a repeat could be recognised
    /// by, and is answered with no warning rather than refused.
    /// </summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task Without_both_a_supplier_and_an_invoice_number_there_is_no_warning(
        bool withSupplier, bool withInvoiceNumber)
    {
        var invoiceNumber = $"INV-DUP-{Guid.NewGuid():N}";
        await Created(NewKey(), invoiceNumber: invoiceNumber, supplierId: _factory.SupplierId());

        var match = await DuplicateAsync(
            withInvoiceNumber ? invoiceNumber : null,
            withSupplier ? _factory.SupplierId() : null,
            PurchaseDate);

        Assert.Equal(JsonValueKind.Null, match.ValueKind);
    }

    /// <summary>The tenant boundary: another business's purchase is never what this one is warned about.</summary>
    [Fact]
    public async Task Another_businesss_purchase_never_produces_a_warning()
    {
        var invoiceNumber = $"INV-DUP-{Guid.NewGuid():N}";
        await Created(NewKey(), invoiceNumber: invoiceNumber, supplierId: _factory.SupplierId());

        var response = await _factory.BusinessB().GetAsync(DuplicateUri(invoiceNumber, _factory.SupplierId(), PurchaseDate));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("match").ValueKind);
    }

    /// <summary>The purchase date every form in this file submits unless it says otherwise.</summary>
    private static readonly DateTime PurchaseDate = new(2026, 4, 1, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Midnight at the start of 20 April 2026 in the fixture business's own time zone
    /// (Australia/Sydney, UTC+10 once daylight saving has ended), as the UTC instant a client sends.
    /// </summary>
    private static readonly DateTime BusinessDayStart = new(2026, 4, 19, 14, 0, 0, DateTimeKind.Utc);

    private static string NewKey() => Guid.NewGuid().ToString();

    private async Task<int> Created(string? key, string? invoiceNumber = null, int? supplierId = null)
    {
        var response = await _factory.BusinessA().PostAsync(
            PurchasesUri, await FormAsync(key, invoiceNumber: invoiceNumber, supplierId: supplierId));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await PurchaseAsync(response)).GetProperty("id").GetInt32();
    }

    /// <summary>One purchase as <c>GET api/purchases/{id}</c> returns it.</summary>
    private async Task<JsonElement> ReadAsync(int purchaseId)
    {
        var response = await _factory.BusinessA().GetAsync($"{PurchasesUri}/{purchaseId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await PurchaseAsync(response);
    }

    private async Task<JsonElement> DuplicateAsync(string? invoiceNumber, int? supplierId, DateTime purchaseDate)
    {
        var response = await _factory.BusinessA().GetAsync(DuplicateUri(invoiceNumber, supplierId, purchaseDate));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("match").Clone();
    }

    private static string DuplicateUri(string? invoiceNumber, int? supplierId, DateTime purchaseDate)
    {
        var query = new List<string>
        {
            $"purchaseDate={Uri.EscapeDataString(purchaseDate.ToString("O", CultureInfo.InvariantCulture))}",
        };
        if (supplierId is { } supplier) query.Add($"supplierId={supplier}");
        if (invoiceNumber is not null) query.Add($"invoiceNumber={Uri.EscapeDataString(invoiceNumber)}");
        return $"{PurchasesUri}/possible-duplicate?{string.Join("&", query)}";
    }

    /// <summary>
    /// The create form. <paramref name="product"/> is <c>false</c> for Business B, which has its own
    /// catalogue: a purchase there names its own product rather than Business A's.
    /// <paramref name="document"/> are the receipt's own bytes, sent under one fixed name so two
    /// forms differ by their bytes alone.
    /// </summary>
    private async Task<MultipartFormDataContent> FormAsync(
        string? key,
        string? invoiceNumber = null,
        int? supplierId = null,
        bool product = true,
        byte[]? document = null,
        DateTime? purchaseDate = null)
    {
        var productId = product ? await _factory.ProductIdAsync() : await _factory.OtherBusinessProductIdAsync();
        var content = new MultipartFormDataContent
        {
            { new StringContent("Weekly restock"), "title" },
            { new StringContent((purchaseDate ?? PurchaseDate).ToString("O", CultureInfo.InvariantCulture)), "purchaseDate" },
            { new StringContent($"[{{\"productId\":{productId},\"quantity\":2,\"unitCost\":1.10}}]"), "items" },
        };
        if (document is not null)
        {
            var part = new ByteArrayContent(document);
            part.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            content.Add(part, "file", "scan.jpg");
        }

        if (key is not null) content.Add(new StringContent(key), "idempotencyKey");
        if (invoiceNumber is not null) content.Add(new StringContent(invoiceNumber), "invoiceNumber");
        if (supplierId is { } supplier)
            content.Add(new StringContent(supplier.ToString(CultureInfo.InvariantCulture)), "supplierId");
        return content;
    }

    /// <summary>The <c>purchase</c> member of the endpoint's response envelope.</summary>
    private static async Task<JsonElement> PurchaseAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("purchase").Clone();
    }

    /// <summary>
    /// The API hosted the way the end-to-end harness hosts it, over a disposable in-memory SQLite
    /// database, with document storage replaced by the in-memory double so a receipt sent with a
    /// create writes no file anywhere on this machine and a refused create can be shown to have
    /// stored none.
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

        /// <summary>
        /// The same double <c>PurchaseWithoutDocumentApiTests</c> uses, so "what is in storage" means
        /// the same thing in both files.
        /// </summary>
        public PurchaseWithoutDocumentApiTests.InMemoryDocumentStorage Documents { get; } = new();

        public HttpClient BusinessA() => As(E2ETestActors.BusinessAOwner.Key);

        public HttpClient BusinessB() => As(E2ETestActors.BusinessBOwner.Key);

        public async Task<long> ProductIdAsync()
        {
            await using var db = Database();
            return await db.Products.AsNoTracking()
                .Where(product => product.Name == E2ETestFixture.PurchaseProductName)
                .Select(product => product.Id)
                .SingleAsync();
        }

        /// <summary>A product Business B owns, so its own purchase names a product of its own catalogue.</summary>
        public async Task<long> OtherBusinessProductIdAsync()
        {
            await using var db = Database();
            return await db.Products.AsNoTracking()
                .Where(product => product.Name == E2ETestFixture.BusinessBProductName)
                .Select(product => product.Id)
                .SingleAsync();
        }

        /// <summary>The product's physical stock, read from the database.</summary>
        public async Task<int> ProductStockAsync(long productId)
        {
            await using var db = Database();
            return await db.Products.AsNoTracking()
                .Where(product => product.Id == productId)
                .Select(product => product.QuantityInStock)
                .SingleAsync();
        }

        /// <summary>A supplier of Business A, and a second one, for the duplicate-warning cases.</summary>
        public int SupplierId() => SupplierIds()[0];

        public int OtherSupplierId() => SupplierIds()[1];

        public async Task<string?> StoredKeyAsync(int purchaseId)
        {
            await using var db = Database();
            return await db.Receipts.AsNoTracking()
                .Where(receipt => receipt.Id == purchaseId)
                .Select(receipt => receipt.IdempotencyKey)
                .SingleAsync();
        }

        /// <summary>How many purchases - of any business - hold this idempotency key.</summary>
        public async Task<int> PurchaseCountForKeyAsync(string key)
        {
            await using var db = Database();
            return await db.Receipts.AsNoTracking().CountAsync(receipt => receipt.IdempotencyKey == key);
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

        /// <summary>Every stock movement in the database, for the same reason.</summary>
        public async Task<int> StockMovementCountAsync()
        {
            await using var db = Database();
            return await db.StockAdjustments.AsNoTracking().CountAsync();
        }

        /// <summary>The restock movements one purchase recorded.</summary>
        public async Task<int> RestockMovementCountAsync(int purchaseId)
        {
            await using var db = Database();
            var itemIds = await db.ReceiptItems.AsNoTracking()
                .Where(item => item.ReceiptId == purchaseId)
                .Select(item => item.Id)
                .ToListAsync();
            return await db.StockAdjustments.AsNoTracking()
                .CountAsync(movement => movement.ReceiptItemId.HasValue && itemIds.Contains(movement.ReceiptItemId.Value));
        }

        private int[]? _supplierIds;

        /// <summary>
        /// Two suppliers of Business A. The fixture seeds it one, so the second is added here -
        /// directly rather than over HTTP, because this file is about the create and the warning and
        /// a supplier is only what they are keyed by.
        /// </summary>
        private int[] SupplierIds()
        {
            if (_supplierIds is not null) return _supplierIds;

            using var db = Database();
            var businessId = db.Products.AsNoTracking()
                .Where(product => product.Name == E2ETestFixture.PurchaseProductName)
                .Select(product => product.BusinessId)
                .Single();
            var existing = db.Suppliers.AsNoTracking()
                .Where(supplier => supplier.BusinessId == businessId)
                .OrderBy(supplier => supplier.Id)
                .Select(supplier => supplier.Id)
                .Take(2)
                .ToList();
            while (existing.Count < 2)
            {
                var supplier = new Supplier
                {
                    BusinessId = businessId,
                    Name = $"Duplicate warning supplier {existing.Count + 1}",
                };
                db.Suppliers.Add(supplier);
                db.SaveChanges();
                existing.Add(supplier.Id);
            }

            return _supplierIds = [.. existing];
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
                    d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                if (dbContextOptions is not null)
                {
                    services.Remove(dbContextOptions);
                }

                services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));
            });

            // Registered last, so an uploaded receipt never reaches the filesystem adapter.
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
        /// An unrestricted context for arranging and verifying both businesses' rows from outside the
        /// boundary, never over HTTP. Resolving <c>Services</c> starts the host, which is what creates
        /// the schema and seeds the fixture this then reads.
        /// </summary>
        private AppDbContext Database()
        {
            _ = Services;
            return TestAppDbContext.Unrestricted(
                new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        }
    }
}
