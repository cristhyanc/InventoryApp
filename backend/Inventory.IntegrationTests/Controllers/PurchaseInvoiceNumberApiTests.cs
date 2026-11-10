using System.Globalization;
using System.Net;
using System.Text.Json;
using Inventory.Domain.Purchases;
using Inventory.Infrastructure.Data;
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
/// A purchase records the supplier's own receipt or invoice number, and a user can see it on the
/// purchase and in the Purchases list (issue #1162, sub-issue A of #975).
///
/// <para>These run over the real HTTP pipeline - a genuine <c>multipart/form-data</c> body, the
/// framework's own binding, authentication, the business-scope middleware and relational SQLite -
/// because what is being proved is the request/response contract and the tenant boundary, neither of
/// which a directly constructed controller call shows.</para>
/// </summary>
public sealed class PurchaseInvoiceNumberApiTests : IClassFixture<PurchaseInvoiceNumberApiTests.ApiFactory>
{
    private const string PurchasesUri = "/api/purchases";

    private readonly ApiFactory _factory;

    public PurchaseInvoiceNumberApiTests(ApiFactory factory) => _factory = factory;

    /// <summary>The issue's first criterion: " INV-123 " is stored and returned as "INV-123".</summary>
    [Fact]
    public async Task A_created_purchase_returns_its_invoice_number_trimmed()
    {
        var response = await _factory.BusinessA().PostAsync(
            PurchasesUri, await FormAsync(invoiceNumber: " INV-123 "));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var purchase = await PurchaseAsync(response);
        Assert.Equal("INV-123", purchase.GetProperty("invoiceNumber").GetString());

        // Stored trimmed as well as reported trimmed, and readable back on the purchase itself.
        var id = purchase.GetProperty("id").GetInt32();
        Assert.Equal("INV-123", await _factory.StoredInvoiceNumberAsync(id));
        Assert.Equal("INV-123", (await ReadAsync(id)).GetProperty("invoiceNumber").GetString());
    }

    /// <summary>
    /// Blank is none, not an empty string: a purchase nobody typed a number on reports <c>null</c>,
    /// the same answer a purchase recorded before this feature existed gives.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_invoice_number_is_stored_as_none(string? submitted)
    {
        var response = await _factory.BusinessA().PostAsync(PurchasesUri, await FormAsync(submitted));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var purchase = await PurchaseAsync(response);
        Assert.Equal(JsonValueKind.Null, purchase.GetProperty("invoiceNumber").ValueKind);
        Assert.Null(await _factory.StoredInvoiceNumberAsync(purchase.GetProperty("id").GetInt32()));
    }

    [Fact]
    public async Task An_invoice_number_of_the_maximum_length_is_accepted()
    {
        var longest = new string('9', PurchaseInvoiceNumberPolicy.MaxLength);

        var response = await _factory.BusinessA().PostAsync(PurchasesUri, await FormAsync(longest));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(longest, (await PurchaseAsync(response)).GetProperty("invoiceNumber").GetString());
    }

    /// <summary>
    /// One character past the limit is refused with the reason, and the purchase is not recorded at
    /// all: an over-long number must not be truncated into a different invoice number, and the
    /// refusal must not leave half a purchase behind.
    /// </summary>
    [Fact]
    public async Task An_over_long_invoice_number_is_refused_and_saves_nothing()
    {
        var purchasesBefore = await _factory.PurchaseCountAsync();

        var response = await _factory.BusinessA().PostAsync(
            PurchasesUri, await FormAsync(new string('9', PurchaseInvoiceNumberPolicy.MaxLength + 1)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(PurchaseInvoiceNumberPolicy.TooLongMessage, await response.Content.ReadAsStringAsync());
        Assert.Equal(purchasesBefore, await _factory.PurchaseCountAsync());
    }

    /// <summary>The Purchases list carries the number, so the list can show it without a second read.</summary>
    [Fact]
    public async Task The_purchases_list_carries_each_purchases_invoice_number()
    {
        var withNumber = await CreateAsync("INV-LIST-1");
        var withoutNumber = await CreateAsync(null);

        var response = await _factory.BusinessA().GetAsync(PurchasesUri);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var listed = await ListedInvoiceNumbersAsync(response);
        Assert.Equal("INV-LIST-1", listed[withNumber]);
        Assert.Null(listed[withoutNumber]);
    }

    /// <summary>
    /// The edit saves a changed number and a cleared one, and changes nothing else: the purchase's own
    /// amounts, its line, its restock movement and its product's whole costing position are where they
    /// were - an invoice number is a reference to paperwork, not money.
    /// </summary>
    [Fact]
    public async Task An_edited_invoice_number_is_saved_and_moves_no_stock_or_cost()
    {
        var purchaseId = await CreateAsync("INV-FIRST");
        var productId = await _factory.ProductIdAsync(E2ETestFixture.PurchaseProductName);
        var costingBefore = await _factory.ProductCostingAsync(productId);
        var movementBefore = await _factory.RestockMovementAsync(purchaseId);
        var before = await ReadAsync(purchaseId);

        var changed = await EditAsync(purchaseId, before, " INV-SECOND ");

        Assert.Equal("INV-SECOND", changed.GetProperty("invoiceNumber").GetString());
        Assert.Equal(await _factory.ProductCostingAsync(productId), costingBefore);
        Assert.Equal(await _factory.RestockMovementAsync(purchaseId), movementBefore);
        AssertNothingElseChanged(before, changed);

        var cleared = await EditAsync(purchaseId, before, "   ");

        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("invoiceNumber").ValueKind);
        Assert.Null(await _factory.StoredInvoiceNumberAsync(purchaseId));
        Assert.Equal(await _factory.ProductCostingAsync(productId), costingBefore);
        Assert.Equal(await _factory.RestockMovementAsync(purchaseId), movementBefore);
        AssertNothingElseChanged(before, cleared);
    }

    /// <summary>
    /// An edit submitting an over-long number is refused, and the purchase keeps the number it had:
    /// the refusal happens before the edit's transaction opens.
    /// </summary>
    [Fact]
    public async Task An_over_long_invoice_number_is_refused_on_an_edit_and_changes_nothing()
    {
        var purchaseId = await CreateAsync("INV-KEEP");
        var before = await ReadAsync(purchaseId);

        var response = await EditResponseAsync(
            purchaseId, before, new string('9', PurchaseInvoiceNumberPolicy.MaxLength + 1));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(PurchaseInvoiceNumberPolicy.TooLongMessage, await response.Content.ReadAsStringAsync());
        Assert.Equal("INV-KEEP", await _factory.StoredInvoiceNumberAsync(purchaseId));
    }

    /// <summary>
    /// A purchase recorded before this feature keeps <c>null</c> and reads exactly as it did: the
    /// column is the only thing the upgrade added. Written straight into the database so it really is
    /// a row no request ever gave a number to.
    /// </summary>
    [Fact]
    public async Task A_purchase_that_predates_the_field_reports_no_invoice_number()
    {
        // Anchored on a purchase this business really owns, so the inserted row is one of its own.
        var ownPurchaseId = await CreateAsync("INV-ANCHOR");
        var purchaseId = await _factory.InsertPurchaseWithoutInvoiceNumberAsync("Recorded earlier", ownPurchaseId);

        var purchase = await ReadAsync(purchaseId);

        Assert.Equal("Recorded earlier", purchase.GetProperty("title").GetString());
        Assert.Equal(JsonValueKind.Null, purchase.GetProperty("invoiceNumber").ValueKind);
    }

    /// <summary>
    /// The tenant boundary: another business's purchase is simply not there, so neither its invoice
    /// number nor the fact that it has one is readable - not by id, and not in the list.
    /// </summary>
    [Fact]
    public async Task Another_businesss_invoice_number_is_never_returned()
    {
        var businessAPurchaseId = await CreateAsync("INV-PRIVATE");

        var read = await _factory.BusinessB().GetAsync($"{PurchasesUri}/{businessAPurchaseId}");
        var list = await _factory.BusinessB().GetAsync(PurchasesUri);
        var listBody = await list.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.DoesNotContain("INV-PRIVATE", listBody, StringComparison.Ordinal);
        Assert.DoesNotContain(businessAPurchaseId, (await ListedInvoiceNumbersAsync(list)).Keys);

        // And an edit from the other business cannot reach it either: the number stays as it was.
        var editAttempt = await EditResponseAsync(
            businessAPurchaseId, await ReadAsync(businessAPurchaseId), "INV-TAKEN", _factory.BusinessB());

        Assert.Equal(HttpStatusCode.NotFound, editAttempt.StatusCode);
        Assert.Equal("INV-PRIVATE", await _factory.StoredInvoiceNumberAsync(businessAPurchaseId));
    }

    /// <summary>
    /// Every value on the purchase except its invoice number: its own fields, and each stored line's
    /// identity, quantity, cost, extended total and GST classification.
    ///
    /// <para>A line's nested catalogue <c>product</c> snapshot is deliberately not compared. It is the
    /// product's own row as the read loaded it, and a purchase edit rebuilds the affected product's
    /// cost and stamps that row's <c>updatedAt</c> whatever was edited - which is why what the product
    /// actually holds, its stock, costing quantity, inventory value and weighted-average cost, is
    /// compared from the database instead, beside the purchase's own restock movement.</para>
    /// </summary>
    private static void AssertNothingElseChanged(JsonElement before, JsonElement after)
    {
        Assert.Equal(OwnFields(before), OwnFields(after));
        Assert.Equal(Lines(before), Lines(after));

        static string OwnFields(JsonElement purchase) =>
            string.Join(
                "|",
                purchase.EnumerateObject()
                    .Where(property => property.Name is not ("invoiceNumber" or "items"))
                    .Select(property => $"{property.Name}={property.Value.GetRawText()}"));

        static string Lines(JsonElement purchase) =>
            string.Join("|", purchase.GetProperty("items").EnumerateArray().Select(item =>
                string.Join(
                    ",",
                    item.EnumerateObject()
                        .Where(property => property.Name != "product")
                        .Select(property => $"{property.Name}={property.Value.GetRawText()}"))));
    }

    private async Task<int> CreateAsync(string? invoiceNumber)
    {
        var response = await _factory.BusinessA().PostAsync(PurchasesUri, await FormAsync(invoiceNumber));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await PurchaseAsync(response)).GetProperty("id").GetInt32();
    }

    private async Task<JsonElement> ReadAsync(int purchaseId)
    {
        var response = await _factory.BusinessA().GetAsync($"{PurchasesUri}/{purchaseId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await PurchaseAsync(response);
    }

    /// <summary>
    /// The purchase edited with a new invoice number and everything else exactly as it was read back,
    /// its own line ids included - which is what makes "no other field, stock movement or cost
    /// changed" a claim about the edit rather than about an emptied purchase.
    /// </summary>
    private async Task<JsonElement> EditAsync(int purchaseId, JsonElement current, string? invoiceNumber)
    {
        var response = await EditResponseAsync(purchaseId, current, invoiceNumber);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await PurchaseAsync(response);
    }

    private async Task<HttpResponseMessage> EditResponseAsync(
        int purchaseId, JsonElement current, string? invoiceNumber, HttpClient? client = null)
    {
        var content = new MultipartFormDataContent
        {
            { new StringContent(current.GetProperty("title").GetString()!), "title" },
            { new StringContent(current.GetProperty("purchaseDate").GetString()!), "purchaseDate" },
            { new StringContent(ItemsOf(current)), "items" },
        };
        if (invoiceNumber is not null) content.Add(new StringContent(invoiceNumber), "invoiceNumber");

        return await (client ?? _factory.BusinessA()).PutAsync($"{PurchasesUri}/{purchaseId}", content);
    }

    /// <summary>
    /// The purchase's stored lines, as the edit endpoint accepts them back. Formatted invariantly,
    /// because this is JSON rather than something a person reads.
    /// </summary>
    private static string ItemsOf(JsonElement purchase) =>
        "[" + string.Join(",", purchase.GetProperty("items").EnumerateArray().Select(item =>
            $"{{\"id\":{item.GetProperty("id").GetInt32()}," +
            $"\"productId\":{item.GetProperty("productId").GetInt64()}," +
            $"\"quantity\":{Number(item, "quantity")}," +
            $"\"unitCost\":{Number(item, "unitCost")}}}")) + "]";

    private static string Number(JsonElement item, string property) =>
        item.GetProperty(property).GetDecimal().ToString(CultureInfo.InvariantCulture);

    private async Task<MultipartFormDataContent> FormAsync(string? invoiceNumber)
    {
        var productId = await _factory.ProductIdAsync(E2ETestFixture.PurchaseProductName);
        var content = new MultipartFormDataContent
        {
            { new StringContent("Weekly restock"), "title" },
            { new StringContent("2026-04-01T10:00:00Z"), "purchaseDate" },
            { new StringContent($"[{{\"productId\":{productId},\"quantity\":2,\"unitCost\":1.10}}]"), "items" },
        };
        if (invoiceNumber is not null) content.Add(new StringContent(invoiceNumber), "invoiceNumber");
        return content;
    }

    /// <summary>The <c>purchase</c> member of the endpoint's response envelope.</summary>
    private static async Task<JsonElement> PurchaseAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("purchase").Clone();
    }

    /// <summary>Each listed purchase's id and the invoice number the list reports for it.</summary>
    private static async Task<Dictionary<int, string?>> ListedInvoiceNumbersAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.EnumerateArray()
            .Select(envelope => envelope.GetProperty("purchase"))
            .ToDictionary(
                purchase => purchase.GetProperty("id").GetInt32(),
                purchase => purchase.GetProperty("invoiceNumber").GetString());
    }

    /// <summary>
    /// The API hosted the way the end-to-end harness hosts it, over a disposable in-memory SQLite
    /// database. No document storage double is needed: every purchase here is recorded without a
    /// document, so nothing ever reaches the storage adapter at all.
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

        public HttpClient BusinessA() => As(E2ETestActors.BusinessAOwner.Key);

        public HttpClient BusinessB() => As(E2ETestActors.BusinessBOwner.Key);

        public async Task<long> ProductIdAsync(string productName)
        {
            await using var db = Database();
            return (await db.Products.AsNoTracking().SingleAsync(product => product.Name == productName)).Id;
        }

        /// <summary>
        /// The invoice number the row itself holds, read outside the API, so "stored trimmed" and
        /// "cleared" are claims about the database rather than about a response.
        /// </summary>
        public async Task<string?> StoredInvoiceNumberAsync(int purchaseId)
        {
            await using var db = Database();
            return await db.Receipts.AsNoTracking()
                .Where(receipt => receipt.Id == purchaseId)
                .Select(receipt => receipt.InvoiceNumber)
                .SingleAsync();
        }

        /// <summary>
        /// A purchase inserted straight into Business A's rows with no invoice number at all - the
        /// state every purchase recorded before issue #1162 is in.
        /// </summary>
        public async Task<int> InsertPurchaseWithoutInvoiceNumberAsync(string title, int sameBusinessAsPurchaseId)
        {
            await using var db = Database();
            var businessId = await db.Receipts.AsNoTracking()
                .Where(receipt => receipt.Id == sameBusinessAsPurchaseId)
                .Select(receipt => receipt.BusinessId)
                .SingleAsync();
            var purchase = new Inventory.Infrastructure.Models.Purchase
            {
                BusinessId = businessId,
                Title = title,
                PurchaseDate = new DateTime(2026, 2, 1, 9, 0, 0, DateTimeKind.Utc),
                CreatedAt = new DateTime(2026, 2, 1, 9, 0, 0, DateTimeKind.Utc),
            };
            db.Receipts.Add(purchase);
            await db.SaveChangesAsync();
            return purchase.Id;
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

        /// <summary>The product's physical stock and its whole costing position, read from the database.</summary>
        public async Task<string> ProductCostingAsync(long productId)
        {
            await using var db = Database();
            return await db.Products.AsNoTracking()
                .Where(product => product.Id == productId)
                .Select(product =>
                    $"{product.QuantityInStock}:{product.CostingQuantity}:{product.InventoryValue}:" +
                    $"{product.AverageUnitCost}")
                .SingleAsync();
        }

        /// <summary>
        /// The restock movements one purchase recorded, as one comparable value: the product, the
        /// reason, the quantity, the costs, the running position and the effective instant.
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
                .OrderBy(movement => movement.Id)
                .Select(movement => $"{movement.ProductId}:{movement.Reason}:{movement.QuantityChange}:" +
                    $"{movement.UnitCost}:{movement.TotalCost}:{movement.CostingQuantityAfter}:" +
                    $"{movement.InventoryValueAfter}:{movement.AverageUnitCostAfter}:{movement.EffectiveAt:O}")
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
                var dbContextOptions = services.SingleOrDefault(
                    d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
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
