using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Inventory.Domain.Categories;
using Inventory.Domain.Expenses;
using Inventory.Domain.FieldLengths;
using Inventory.Domain.Machines;
using Inventory.Domain.Products;
using Inventory.Domain.Purchases;
using Inventory.Domain.Suppliers;
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
/// The free-text length limits of issue #1286 (audit #1226 finding DB-03), one record type at a time,
/// over the real HTTP pipeline and relational SQLite: a create or update carrying a value one
/// character over its limit is refused with <c>400</c> naming the field and the limit and writes
/// nothing; a value exactly at the limit is accepted; and a row that already holds a longer value -
/// written straight into the database, the state a row saved before the limits is in - can still be
/// saved with another field changed, while a new over-long value for it is refused.
/// </summary>
public sealed class FieldLengthLimitsApiTests : IClassFixture<FieldLengthLimitsApiTests.ApiFactory>
{
    private readonly ApiFactory _factory;

    public FieldLengthLimitsApiTests(ApiFactory factory) => _factory = factory;

    private static string TooLong(int max) => new('x', max + 1);

    private static string AtLimit(int max) => new('a', max);

    private static string Legacy(int max) => new('L', max + 25);

    // ---- Products -------------------------------------------------------------------------------

    [Fact]
    public async Task Products_refuse_an_over_long_value_on_create_and_write_nothing()
    {
        var before = await _factory.CountAsync(db => db.Products);

        var response = await _factory.Client().PostAsJsonAsync(
            "/api/products", ProductCreate("Length product", description: TooLong(ProductFieldLengthPolicy.MaxDescriptionLength)));

        await AssertRefusedAsync(
            response, FieldLengthLimit.TooLongMessage("Description", ProductFieldLengthPolicy.MaxDescriptionLength));
        Assert.Equal(before, await _factory.CountAsync(db => db.Products));
    }

    [Fact]
    public async Task Products_accept_values_exactly_at_each_limit()
    {
        var response = await _factory.Client().PostAsJsonAsync(
            "/api/products",
            ProductCreate(
                AtLimit(ProductFieldLengthPolicy.MaxNameLength),
                sku: AtLimit(ProductFieldLengthPolicy.MaxSkuLength),
                unit: AtLimit(ProductFieldLengthPolicy.MaxUnitLength),
                description: AtLimit(ProductFieldLengthPolicy.MaxDescriptionLength)));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Products_refuse_an_over_long_value_on_update_and_keep_the_row()
    {
        var id = await CreateProductAsync("Product to edit");

        var response = await _factory.Client().PutAsJsonAsync(
            $"/api/products/{id}", ProductUpdate("Product to edit", sku: TooLong(ProductFieldLengthPolicy.MaxSkuLength)));

        await AssertRefusedAsync(response, FieldLengthLimit.TooLongMessage("SKU", ProductFieldLengthPolicy.MaxSkuLength));
        Assert.Null(await _factory.ReadAsync(db => db.Products.Where(p => p.Id == id).Select(p => p.Sku)));
    }

    [Fact]
    public async Task A_product_already_holding_a_longer_value_can_still_be_edited()
    {
        var id = await CreateProductAsync("Legacy product");
        var legacy = Legacy(ProductFieldLengthPolicy.MaxDescriptionLength);
        await _factory.WriteAsync(db => db.Products.Where(p => p.Id == id)
            .ExecuteUpdateAsync(set => set.SetProperty(p => p.Description, legacy)));

        var kept = await _factory.Client().PutAsJsonAsync(
            $"/api/products/{id}", ProductUpdate("Legacy product", sku: "SKU-NEW", description: legacy));
        var changed = await _factory.Client().PutAsJsonAsync(
            $"/api/products/{id}", ProductUpdate("Legacy product", description: legacy[1..]));

        Assert.True(kept.IsSuccessStatusCode, await kept.Content.ReadAsStringAsync());
        await AssertRefusedAsync(
            changed, FieldLengthLimit.TooLongMessage("Description", ProductFieldLengthPolicy.MaxDescriptionLength));
        Assert.Equal(
            ("SKU-NEW", legacy),
            await _factory.ReadAsync(db => db.Products.Where(p => p.Id == id).Select(p => ValueTuple.Create(p.Sku, p.Description))));
    }

    // ---- Categories -----------------------------------------------------------------------------

    [Fact]
    public async Task Categories_refuse_an_over_long_value_on_create_and_write_nothing()
    {
        var before = await _factory.CountAsync(db => db.Categories);

        var response = await _factory.Client().PostAsJsonAsync(
            "/api/categories", new { name = TooLong(CategoryFieldLengthPolicy.MaxNameLength), description = (string?)null });

        await AssertRefusedAsync(response, FieldLengthLimit.TooLongMessage("Category name", CategoryFieldLengthPolicy.MaxNameLength));
        Assert.Equal(before, await _factory.CountAsync(db => db.Categories));
    }

    [Fact]
    public async Task Categories_accept_values_exactly_at_each_limit()
    {
        var response = await _factory.Client().PostAsJsonAsync(
            "/api/categories",
            new
            {
                name = AtLimit(CategoryFieldLengthPolicy.MaxNameLength),
                description = AtLimit(CategoryFieldLengthPolicy.MaxDescriptionLength),
            });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Categories_refuse_an_over_long_value_on_update_and_keep_the_row()
    {
        var id = await CreateAsync("/api/categories", new { name = "Category to edit", description = "Kept" });

        var response = await _factory.Client().PutAsJsonAsync(
            $"/api/categories/{id}",
            new { name = "Category to edit", description = TooLong(CategoryFieldLengthPolicy.MaxDescriptionLength) });

        await AssertRefusedAsync(
            response, FieldLengthLimit.TooLongMessage("Description", CategoryFieldLengthPolicy.MaxDescriptionLength));
        Assert.Equal("Kept", await _factory.ReadAsync(db => db.Categories.Where(c => c.Id == id).Select(c => c.Description)));
    }

    [Fact]
    public async Task A_category_already_holding_a_longer_name_can_still_be_edited()
    {
        var id = await CreateAsync("/api/categories", new { name = "Legacy category", description = (string?)null });
        var legacy = Legacy(CategoryFieldLengthPolicy.MaxNameLength);
        await _factory.WriteAsync(db => db.Categories.Where(c => c.Id == id)
            .ExecuteUpdateAsync(set => set.SetProperty(c => c.Name, legacy)));

        var kept = await _factory.Client().PutAsJsonAsync($"/api/categories/{id}", new { name = legacy, description = "New" });
        var changed = await _factory.Client().PutAsJsonAsync($"/api/categories/{id}", new { name = legacy[1..], description = "New" });

        Assert.Equal(HttpStatusCode.NoContent, kept.StatusCode);
        await AssertRefusedAsync(changed, FieldLengthLimit.TooLongMessage("Category name", CategoryFieldLengthPolicy.MaxNameLength));
        Assert.Equal(
            (legacy, "New"),
            await _factory.ReadAsync(db => db.Categories.Where(c => c.Id == id).Select(c => ValueTuple.Create(c.Name, c.Description))));
    }

    // ---- Suppliers ------------------------------------------------------------------------------

    [Fact]
    public async Task Suppliers_refuse_an_over_long_value_on_create_and_write_nothing()
    {
        var before = await _factory.CountAsync(db => db.Suppliers);

        var response = await _factory.Client().PostAsJsonAsync(
            "/api/suppliers", Supplier("Length supplier", phone: TooLong(SupplierFieldLengthPolicy.MaxPhoneLength)));

        var message = FieldLengthLimit.TooLongMessage("Phone", SupplierFieldLengthPolicy.MaxPhoneLength);
        await AssertRefusedAsync(response, message);
        Assert.Equal(message, FieldErrors(await response.Content.ReadAsStringAsync(), "phone"));
        Assert.Equal(before, await _factory.CountAsync(db => db.Suppliers));
    }

    [Fact]
    public async Task Suppliers_accept_values_exactly_at_each_limit()
    {
        var response = await _factory.Client().PostAsJsonAsync(
            "/api/suppliers",
            Supplier(
                AtLimit(SupplierFieldLengthPolicy.MaxNameLength),
                contactName: AtLimit(SupplierFieldLengthPolicy.MaxContactNameLength),
                phone: AtLimit(SupplierFieldLengthPolicy.MaxPhoneLength),
                email: AtLimit(SupplierFieldLengthPolicy.MaxEmailLength),
                address: AtLimit(SupplierFieldLengthPolicy.MaxAddressLength)));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Suppliers_refuse_an_over_long_value_on_update_and_keep_the_row()
    {
        var id = await CreateAsync("/api/suppliers", Supplier("Supplier to edit", email: "kept@example.test"));

        var response = await _factory.Client().PutAsJsonAsync(
            $"/api/suppliers/{id}", Supplier("Supplier to edit", email: TooLong(SupplierFieldLengthPolicy.MaxEmailLength)));

        await AssertRefusedAsync(response, FieldLengthLimit.TooLongMessage("Email", SupplierFieldLengthPolicy.MaxEmailLength));
        Assert.Equal("kept@example.test", await _factory.ReadAsync(db => db.Suppliers.Where(s => s.Id == id).Select(s => s.Email)));
    }

    [Fact]
    public async Task A_supplier_already_holding_a_longer_address_can_still_be_edited()
    {
        var id = await CreateAsync("/api/suppliers", Supplier("Legacy supplier"));
        var legacy = Legacy(SupplierFieldLengthPolicy.MaxAddressLength);
        await _factory.WriteAsync(db => db.Suppliers.Where(s => s.Id == id)
            .ExecuteUpdateAsync(set => set.SetProperty(s => s.Address, legacy)));

        var kept = await _factory.Client().PutAsJsonAsync(
            $"/api/suppliers/{id}", Supplier("Legacy supplier", phone: "0400 000 000", address: legacy));
        var changed = await _factory.Client().PutAsJsonAsync(
            $"/api/suppliers/{id}", Supplier("Legacy supplier", address: legacy[1..]));

        Assert.Equal(HttpStatusCode.NoContent, kept.StatusCode);
        await AssertRefusedAsync(changed, FieldLengthLimit.TooLongMessage("Address", SupplierFieldLengthPolicy.MaxAddressLength));
        Assert.Equal(
            ("0400 000 000", legacy),
            await _factory.ReadAsync(db => db.Suppliers.Where(s => s.Id == id).Select(s => ValueTuple.Create(s.Phone, s.Address))));
    }

    // ---- Purchases ------------------------------------------------------------------------------

    [Fact]
    public async Task Purchases_refuse_an_over_long_value_on_create_and_write_nothing()
    {
        var before = await _factory.CountAsync(db => db.Receipts);

        var response = await _factory.Client().PostAsync(
            "/api/purchases", await PurchaseFormAsync("Length purchase", TooLong(PurchaseFieldLengthPolicy.MaxNotesLength)));

        await AssertRefusedAsync(response, FieldLengthLimit.TooLongMessage("Notes", PurchaseFieldLengthPolicy.MaxNotesLength));
        Assert.Equal(before, await _factory.CountAsync(db => db.Receipts));
    }

    [Fact]
    public async Task Purchases_accept_values_exactly_at_each_limit()
    {
        var response = await _factory.Client().PostAsync(
            "/api/purchases",
            await PurchaseFormAsync(
                AtLimit(PurchaseFieldLengthPolicy.MaxTitleLength), AtLimit(PurchaseFieldLengthPolicy.MaxNotesLength)));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Purchases_refuse_an_over_long_value_on_update_and_keep_the_row()
    {
        var id = await CreatePurchaseAsync("Purchase to edit", "Kept notes");

        var response = await _factory.Client().PutAsync(
            $"/api/purchases/{id}", await PurchaseEditFormAsync(id, TooLong(PurchaseFieldLengthPolicy.MaxTitleLength), "Kept notes"));

        await AssertRefusedAsync(response, FieldLengthLimit.TooLongMessage("Title", PurchaseFieldLengthPolicy.MaxTitleLength));
        Assert.Equal(
            ("Purchase to edit", "Kept notes"),
            await _factory.ReadAsync(db => db.Receipts.Where(r => r.Id == id).Select(r => ValueTuple.Create(r.Title, r.Notes))));
    }

    [Fact]
    public async Task A_purchase_already_holding_longer_notes_can_still_be_edited()
    {
        var id = await CreatePurchaseAsync("Legacy purchase", null);
        var legacy = Legacy(PurchaseFieldLengthPolicy.MaxNotesLength);
        await _factory.WriteAsync(db => db.Receipts.Where(r => r.Id == id)
            .ExecuteUpdateAsync(set => set.SetProperty(r => r.Notes, legacy)));

        var kept = await _factory.Client().PutAsync(
            $"/api/purchases/{id}", await PurchaseEditFormAsync(id, "Legacy purchase (renamed)", legacy));
        var changed = await _factory.Client().PutAsync(
            $"/api/purchases/{id}", await PurchaseEditFormAsync(id, "Legacy purchase (renamed)", legacy[1..]));

        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
        await AssertRefusedAsync(changed, FieldLengthLimit.TooLongMessage("Notes", PurchaseFieldLengthPolicy.MaxNotesLength));
        Assert.Equal(
            ("Legacy purchase (renamed)", legacy),
            await _factory.ReadAsync(db => db.Receipts.Where(r => r.Id == id).Select(r => ValueTuple.Create(r.Title, r.Notes))));
    }

    // ---- Operating expenses ---------------------------------------------------------------------

    [Fact]
    public async Task Expenses_refuse_an_over_long_value_on_create_and_write_nothing()
    {
        var before = await _factory.CountAsync(db => db.OperatingExpenses);

        var response = await _factory.Client().PostAsJsonAsync(
            "/api/operating-expenses", Expense(TooLong(OperatingExpenseFieldLengthPolicy.MaxDescriptionLength)));

        await AssertRefusedAsync(
            response, FieldLengthLimit.TooLongMessage("Description", OperatingExpenseFieldLengthPolicy.MaxDescriptionLength));
        Assert.Equal(before, await _factory.CountAsync(db => db.OperatingExpenses));
    }

    [Fact]
    public async Task Expenses_accept_values_exactly_at_each_limit()
    {
        var response = await _factory.Client().PostAsJsonAsync(
            "/api/operating-expenses",
            Expense(
                AtLimit(OperatingExpenseFieldLengthPolicy.MaxDescriptionLength),
                AtLimit(OperatingExpenseFieldLengthPolicy.MaxNotesLength)));

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Expenses_refuse_an_over_long_value_on_update_and_keep_the_row()
    {
        var id = await CreateAsync("/api/operating-expenses", Expense("Expense to edit", "Kept"));

        var response = await _factory.Client().PutAsJsonAsync(
            $"/api/operating-expenses/{id}", Expense("Expense to edit", TooLong(OperatingExpenseFieldLengthPolicy.MaxNotesLength)));

        await AssertRefusedAsync(response, FieldLengthLimit.TooLongMessage("Notes", OperatingExpenseFieldLengthPolicy.MaxNotesLength));
        Assert.Equal("Kept", await _factory.ReadAsync(db => db.OperatingExpenses.Where(e => e.Id == id).Select(e => e.Notes)));
    }

    [Fact]
    public async Task An_expense_already_holding_a_longer_description_can_still_be_edited()
    {
        var id = await CreateAsync("/api/operating-expenses", Expense("Legacy expense"));
        var legacy = Legacy(OperatingExpenseFieldLengthPolicy.MaxDescriptionLength);
        await _factory.WriteAsync(db => db.OperatingExpenses.Where(e => e.Id == id)
            .ExecuteUpdateAsync(set => set.SetProperty(e => e.Description, legacy)));

        var kept = await _factory.Client().PutAsJsonAsync($"/api/operating-expenses/{id}", Expense(legacy, "Paid by card"));
        var changed = await _factory.Client().PutAsJsonAsync($"/api/operating-expenses/{id}", Expense(legacy[1..], "Paid by card"));

        Assert.True(kept.IsSuccessStatusCode, await kept.Content.ReadAsStringAsync());
        await AssertRefusedAsync(
            changed, FieldLengthLimit.TooLongMessage("Description", OperatingExpenseFieldLengthPolicy.MaxDescriptionLength));
        Assert.Equal(
            (legacy, "Paid by card"),
            await _factory.ReadAsync(db => db.OperatingExpenses.Where(e => e.Id == id).Select(e => ValueTuple.Create(e.Description, e.Notes))));
    }

    // ---- Local machines -------------------------------------------------------------------------

    [Fact]
    public async Task Machines_refuse_an_over_long_name_on_create_and_write_nothing()
    {
        var before = await _factory.CountAsync(db => db.Machines);

        var response = await _factory.Client().PostAsJsonAsync(
            "/api/local-machines", new { name = TooLong(MachineFieldLengthPolicy.MaxNameLength) });

        await AssertRefusedAsync(response, FieldLengthLimit.TooLongMessage("Machine name", MachineFieldLengthPolicy.MaxNameLength));
        Assert.Equal(before, await _factory.CountAsync(db => db.Machines));
    }

    [Fact]
    public async Task Machines_accept_a_name_exactly_at_the_limit()
    {
        var response = await _factory.Client().PostAsJsonAsync(
            "/api/local-machines", new { name = AtLimit(MachineFieldLengthPolicy.MaxNameLength) });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Machines_refuse_an_over_long_rename_and_keep_the_row()
    {
        var id = await CreateAsync("/api/local-machines", new { name = "Machine to rename" });

        var response = await _factory.Client().PutAsJsonAsync(
            $"/api/local-machines/{id}", new { name = TooLong(MachineFieldLengthPolicy.MaxNameLength) });

        await AssertRefusedAsync(response, FieldLengthLimit.TooLongMessage("Machine name", MachineFieldLengthPolicy.MaxNameLength));
        Assert.Equal("Machine to rename", await _factory.ReadAsync(db => db.Machines.Where(m => m.Id == id).Select(m => m.Name)));
    }

    [Fact]
    public async Task A_machine_already_holding_a_longer_name_can_still_be_saved_unchanged()
    {
        var id = await CreateAsync("/api/local-machines", new { name = "Legacy machine" });
        var legacy = Legacy(MachineFieldLengthPolicy.MaxNameLength);
        await _factory.WriteAsync(db => db.Machines.Where(m => m.Id == id)
            .ExecuteUpdateAsync(set => set.SetProperty(m => m.Name, legacy)));

        var kept = await _factory.Client().PutAsJsonAsync($"/api/local-machines/{id}", new { name = legacy });
        var changed = await _factory.Client().PutAsJsonAsync($"/api/local-machines/{id}", new { name = legacy[1..] });

        Assert.Equal(HttpStatusCode.NoContent, kept.StatusCode);
        await AssertRefusedAsync(changed, FieldLengthLimit.TooLongMessage("Machine name", MachineFieldLengthPolicy.MaxNameLength));
        Assert.Equal(legacy, await _factory.ReadAsync(db => db.Machines.Where(m => m.Id == id).Select(m => m.Name)));
    }

    // ---- Helpers --------------------------------------------------------------------------------

    /// <summary>A <c>400</c> whose body carries <paramref name="message"/>, whatever envelope the endpoint uses.</summary>
    private static async Task AssertRefusedAsync(HttpResponseMessage response, string message)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"Expected 400, got {(int)response.StatusCode}: {body}");
        Assert.Contains(message, body, StringComparison.Ordinal);
    }

    private static string? FieldErrors(string body, string field)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("errors").GetProperty(field)[0].GetString();
    }

    private static object ProductCreate(string name, string? sku = null, string unit = "each", string? description = null) =>
        new
        {
            name,
            sku,
            description,
            quantityInStock = 0,
            lowStockThreshold = 1,
            restockTo = 5,
            unit,
            categoryId = (long?)null,
            isActive = true,
        };

    private static object ProductUpdate(string name, string? sku = null, string? description = null) =>
        new { name, sku, description, lowStockThreshold = 1, restockTo = 5, unit = "each", categoryId = (long?)null, isActive = true };

    private static object Supplier(
        string name, string? contactName = null, string? phone = null, string? email = null, string? address = null) =>
        new { name, contactName, phone, email, address, url = (string?)null };

    private static object Expense(string description, string? notes = null) =>
        new
        {
            expenseDate = "2026-04-01T00:00:00",
            category = (int)ExpenseCategory.Insurance,
            description,
            amountExGst = 100m,
            gstAmount = 10m,
            totalAmount = 110m,
            notes,
        };

    private async Task<long> CreateProductAsync(string name) =>
        await CreateAsync("/api/products", ProductCreate(name));

    private async Task<long> CreateAsync(string uri, object body)
    {
        var response = await _factory.Client().PostAsJsonAsync(uri, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, text);
        using var document = JsonDocument.Parse(text);
        return document.RootElement.GetProperty("id").GetInt64();
    }

    private async Task<int> CreatePurchaseAsync(string title, string? notes)
    {
        var response = await _factory.Client().PostAsync("/api/purchases", await PurchaseFormAsync(title, notes));
        var text = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var document = JsonDocument.Parse(text);
        return document.RootElement.GetProperty("purchase").GetProperty("id").GetInt32();
    }

    private async Task<MultipartFormDataContent> PurchaseFormAsync(string title, string? notes)
    {
        var productId = await _factory.ReadAsync(db =>
            db.Products.Where(p => p.Name == E2ETestFixture.PurchaseProductName).Select(p => p.Id));
        var content = new MultipartFormDataContent
        {
            { new StringContent(title), "title" },
            { new StringContent("2026-04-01T10:00:00Z"), "purchaseDate" },
            { new StringContent($"[{{\"productId\":{productId},\"quantity\":2,\"unitCost\":1.10}}]"), "items" },
        };
        if (notes is not null) content.Add(new StringContent(notes), "notes");
        return content;
    }

    /// <summary>An edit that sends the purchase's own stored lines back, with the title and notes given.</summary>
    private async Task<MultipartFormDataContent> PurchaseEditFormAsync(int purchaseId, string title, string? notes)
    {
        var lines = await _factory.ReadListAsync(db => db.ReceiptItems
            .Where(item => item.ReceiptId == purchaseId)
            .Select(item => new { item.Id, item.ProductId, item.Quantity, item.UnitCost }));
        var items = "[" + string.Join(",", lines.Select(line => string.Create(
            CultureInfo.InvariantCulture,
            $"{{\"id\":{line.Id},\"productId\":{line.ProductId},\"quantity\":{line.Quantity},\"unitCost\":{line.UnitCost}}}"))) + "]";
        var content = new MultipartFormDataContent
        {
            { new StringContent(title), "title" },
            { new StringContent("2026-04-01T10:00:00Z"), "purchaseDate" },
            { new StringContent(items), "items" },
        };
        if (notes is not null) content.Add(new StringContent(notes), "notes");
        return content;
    }

    /// <summary>
    /// The API hosted the way the end-to-end harness hosts it, over a disposable in-memory SQLite
    /// database, signed in as Business A's Owner. Every row is arranged and read back through an
    /// unrestricted context outside the HTTP boundary.
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

        public HttpClient Client()
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                E2ETestActors.ActorHeaderName, E2ETestActors.BusinessAOwner.Key);
            return client;
        }

        public async Task<int> CountAsync<T>(Func<AppDbContext, IQueryable<T>> rows)
        {
            await using var db = Database();
            return await rows(db).CountAsync();
        }

        public async Task<T> ReadAsync<T>(Func<AppDbContext, IQueryable<T>> query)
        {
            await using var db = Database();
            return await query(db).SingleAsync();
        }

        public async Task<List<T>> ReadListAsync<T>(Func<AppDbContext, IQueryable<T>> query)
        {
            await using var db = Database();
            return await query(db).ToListAsync();
        }

        /// <summary>Writes straight to the database, the way a row saved before the limits looks.</summary>
        public async Task WriteAsync(Func<AppDbContext, Task<int>> write)
        {
            await using var db = Database();
            Assert.Equal(1, await write(db));
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

        /// <summary>
        /// An unrestricted context for arranging and verifying rows from outside the boundary.
        /// Resolving <c>Services</c> starts the host, which creates the schema and seeds the fixture.
        /// </summary>
        private AppDbContext Database()
        {
            _ = Services;
            return TestAppDbContext.Unrestricted(
                new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        }
    }
}
