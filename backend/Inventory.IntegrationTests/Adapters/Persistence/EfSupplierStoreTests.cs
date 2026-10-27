using Inventory.Application.Suppliers;
using Inventory.Domain.Suppliers;
using Inventory.Infrastructure.Persistence;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

public class EfSupplierStoreTests
{
    private const int BusinessA = 1;
    private const int BusinessB = 2;

    private static async Task<DbContextOptions<AppDbContext>> CreateSqliteAsync(SqliteConnection connection)
    {
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var setup = TestAppDbContext.Unrestricted(options);
        await setup.Database.EnsureCreatedAsync();
        return options;
    }

    [Fact]
    public async Task AddAsync_persists_supplier_and_returns_generated_id()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await using var db = TestAppDbContext.For(options, BusinessA);
        var store = new EfSupplierStore(db);

        var created = await store.AddAsync(
            new SupplierFields("Acme", "Jane", "555-1234", "jane@acme.test", "1 Main St", "https://acme.test/trade"),
            CancellationToken.None);

        Assert.True(created.Id > 0);
        Assert.Equal("Acme", created.Name);
        Assert.Equal("https://acme.test/trade", created.Url);

        await using var verify = TestAppDbContext.For(options, BusinessA);
        var persisted = Assert.Single(await verify.Suppliers.ToListAsync());
        Assert.Equal(created.Id, persisted.Id);
        Assert.Equal("Jane", persisted.ContactName);
        Assert.Equal("https://acme.test/trade", persisted.Url);
    }

    /// <summary>
    /// The optional website column (issue #847) over the real relational schema: a supplier stored
    /// without one reads back as <c>null</c> rather than as an empty string, keeps that <c>null</c>
    /// through an update that leaves the field alone, and can be given and then cleared of a URL.
    /// </summary>
    [Fact]
    public async Task A_supplier_with_no_url_reads_back_null_and_can_be_given_and_cleared_of_one()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await using var db = TestAppDbContext.For(options, BusinessA);
        var store = new EfSupplierStore(db);
        var created = await store.AddAsync(new SupplierFields("Acme", "Jane", null, null, null, null), CancellationToken.None);

        Assert.Null(created.Url);
        Assert.Null((await store.FindByIdAsync(created.Id, CancellationToken.None))!.Url);
        Assert.Null(Assert.Single(await store.ListOrderedByNameAsync(CancellationToken.None)).Url);

        // An edit of the other fields that carries the stored null through leaves it null.
        var renamed = await store.UpdateAsync(
            created.Id, new SupplierFields("Acme Renamed", "Jane", null, null, null, null), CancellationToken.None);
        Assert.Null(Updated(renamed).Url);

        var given = await store.UpdateAsync(
            created.Id,
            new SupplierFields("Acme Renamed", "Jane", null, null, null, "https://acme.test"),
            CancellationToken.None);
        Assert.Equal("https://acme.test", Updated(given).Url);
        Assert.Equal("https://acme.test", (await store.FindByIdAsync(created.Id, CancellationToken.None))!.Url);

        var cleared = await store.UpdateAsync(
            created.Id, new SupplierFields("Acme Renamed", "Jane", null, null, null, null), CancellationToken.None);
        Assert.Null(Updated(cleared).Url);

        await using var verify = TestAppDbContext.Unrestricted(options);
        Assert.Null((await verify.Suppliers.SingleAsync()).Url);
    }

    [Fact]
    public async Task UpdateAsync_changes_fields_and_returns_updated_record()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await using var db = TestAppDbContext.For(options, BusinessA);
        var store = new EfSupplierStore(db);
        var created = await store.AddAsync(
            new SupplierFields("Acme", "Jane", "555-1234", "jane@acme.test", "1 Main St", "https://acme.test"),
            CancellationToken.None);

        var updated = await store.UpdateAsync(
            created.Id,
            new SupplierFields("Acme Updated", "John", "555-9999", "john@acme.test", "2 Main St", "https://acme.test/wholesale"),
            CancellationToken.None);

        var record = Updated(updated);
        Assert.Equal(created.Id, record.Id);
        Assert.Equal("Acme Updated", record.Name);
        Assert.Equal("John", record.ContactName);
        Assert.Equal("https://acme.test/wholesale", record.Url);
    }

    [Fact]
    public async Task UpdateAsync_reports_not_found_for_missing_supplier()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await using var db = TestAppDbContext.For(options, BusinessA);
        var store = new EfSupplierStore(db);

        var updated = await store.UpdateAsync(
            999, new SupplierFields("name", null, null, null, null, null), CancellationToken.None);

        Assert.Equal(SupplierWriteOutcome.NotFound, updated.Outcome);
        Assert.Null(updated.Supplier);
    }

    [Fact]
    public async Task DeleteAsync_removes_an_existing_supplier()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await using var db = TestAppDbContext.For(options, BusinessA);
        var store = new EfSupplierStore(db);
        var created = await store.AddAsync(new SupplierFields("Acme", null, null, null, null, null), CancellationToken.None);

        var deleted = await store.DeleteAsync(created.Id, CancellationToken.None);

        Assert.Equal(SupplierWriteOutcome.Success, deleted.Outcome);
        Assert.Empty(await db.Suppliers.ToListAsync());
    }

    [Fact]
    public async Task DeleteAsync_reports_not_found_for_a_missing_supplier()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await using var db = TestAppDbContext.For(options, BusinessA);
        var store = new EfSupplierStore(db);

        var deleted = await store.DeleteAsync(999, CancellationToken.None);

        Assert.Equal(SupplierWriteOutcome.NotFound, deleted.Outcome);
    }

    /// <summary>
    /// Supplier name has no uniqueness constraint today (AppDbContext indexes but does not
    /// enforce uniqueness on Supplier.Name) - this locks in that existing, deliberately
    /// permissive behavior against the real relational schema.
    /// </summary>
    [Fact]
    public async Task Two_suppliers_with_the_same_name_are_both_persisted()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await using var db = TestAppDbContext.For(options, BusinessA);
        var store = new EfSupplierStore(db);

        await store.AddAsync(new SupplierFields("Same Name", null, null, null, null, null), CancellationToken.None);
        await store.AddAsync(new SupplierFields("Same Name", null, null, null, null, null), CancellationToken.None);

        var all = await store.ListOrderedByNameAsync(CancellationToken.None);
        Assert.Equal(2, all.Count);
    }

    /// <summary>
    /// The central tenant filter (issue #64), exercised through this adapter: a supplier owned
    /// by another business is invisible to list/find, and cannot be updated or deleted by id.
    /// </summary>
    [Fact]
    public async Task Another_business_supplier_is_invisible_to_list_find_update_and_delete()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        int otherBusinessSupplierId;
        await using (var seed = TestAppDbContext.For(options, BusinessB))
        {
            var store = new EfSupplierStore(seed);
            var created = await store.AddAsync(
                new SupplierFields("B Supplier", null, null, null, null, "https://b-supplier.test"),
                CancellationToken.None);
            otherBusinessSupplierId = created.Id;
        }

        await using var db = TestAppDbContext.For(options, BusinessA);
        var storeAsA = new EfSupplierStore(db);

        Assert.Empty(await storeAsA.ListOrderedByNameAsync(CancellationToken.None));
        Assert.Null(await storeAsA.FindByIdAsync(otherBusinessSupplierId, CancellationToken.None));
        Assert.Equal(
            SupplierWriteOutcome.NotFound,
            (await storeAsA.UpdateAsync(
                otherBusinessSupplierId,
                new SupplierFields("Hijacked", null, null, null, null, "https://hijacked.test"),
                CancellationToken.None)).Outcome);
        Assert.Equal(
            SupplierWriteOutcome.NotFound,
            (await storeAsA.DeleteAsync(otherBusinessSupplierId, CancellationToken.None)).Outcome);
        Assert.Equal(
            SupplierWriteOutcome.NotFound,
            (await storeAsA.SetHiddenAsync(otherBusinessSupplierId, true, CancellationToken.None)).Outcome);

        await using var verify = TestAppDbContext.Unrestricted(options);
        var stillThere = Assert.Single(await verify.Suppliers.ToListAsync());
        Assert.Equal("B Supplier", stillThere.Name);
        Assert.Equal("https://b-supplier.test", stillThere.Url);
        Assert.False(stillThere.IsHidden);
    }

    /// <summary>
    /// A supplier the business created itself carries no link and is not hidden (issue #934): only
    /// the approval's provisioning copy sets a preset id, and "Not used" is a later decision.
    /// </summary>
    [Fact]
    public async Task A_supplier_the_business_creates_is_unlinked_and_visible()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await using var db = TestAppDbContext.For(options, BusinessA);
        var store = new EfSupplierStore(db);

        var created = await store.AddAsync(
            new SupplierFields("Acme", null, null, null, null, null), CancellationToken.None);

        Assert.Null(created.SupplierPresetId);
        Assert.False(created.IsHidden);

        var refused = await store.SetHiddenAsync(created.Id, true, CancellationToken.None);
        Assert.Equal(SupplierWriteOutcome.Invalid, refused.Outcome);
        Assert.Equal(SupplierPresetLinkPolicy.HideRefusedMessage, refused.ValidationError);
        Assert.False((await store.FindByIdAsync(created.Id, CancellationToken.None))!.IsHidden);
    }

    /// <summary>
    /// The link rules over the real schema (issue #934): a linked copy refuses a rename, a website
    /// change and a delete, writing nothing at all - and the contact details in the same refused
    /// request are not written either.
    /// </summary>
    [Fact]
    public async Task A_linked_supplier_refuses_a_rename_a_website_change_and_a_delete()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        var presetId = await SeedPresetAsync(options, "Coles");
        var supplierId = await SeedLinkedSupplierAsync(options, BusinessA, "Coles", "https://coles.test", presetId);

        await using var db = TestAppDbContext.For(options, BusinessA);
        var store = new EfSupplierStore(db);

        var renamed = await store.UpdateAsync(
            supplierId,
            new SupplierFields("Coles Group", "Pat", null, null, null, "https://coles.test"),
            CancellationToken.None);
        Assert.Equal(SupplierWriteOutcome.Invalid, renamed.Outcome);
        Assert.Equal(SupplierPresetLinkPolicy.RenameRefusedMessage, renamed.ValidationError);

        var rewebbed = await store.UpdateAsync(
            supplierId,
            new SupplierFields("Coles", "Pat", null, null, null, "https://elsewhere.test"),
            CancellationToken.None);
        Assert.Equal(SupplierWriteOutcome.Invalid, rewebbed.Outcome);
        Assert.Equal(SupplierPresetLinkPolicy.WebsiteRefusedMessage, rewebbed.ValidationError);

        var deleted = await store.DeleteAsync(supplierId, CancellationToken.None);
        Assert.Equal(SupplierWriteOutcome.Invalid, deleted.Outcome);
        Assert.Equal(SupplierPresetLinkPolicy.DeleteRefusedMessage, deleted.ValidationError);

        // Nothing at all was written: the row still has its name, its website, its link, and none
        // of the contact details the three refused requests carried.
        await using var verify = TestAppDbContext.Unrestricted(options);
        var stored = await verify.Suppliers.SingleAsync();
        Assert.Equal(
            ("Coles", "https://coles.test", (int?)presetId, (string?)null),
            (stored.Name, stored.Url, stored.SupplierPresetId, stored.ContactName));
    }

    /// <summary>
    /// What a linked copy's owner may still change: the contact details, submitted together with
    /// the stored name and website, which is exactly what the supplier form sends.
    /// </summary>
    [Fact]
    public async Task A_linked_suppliers_contact_details_still_save()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        var presetId = await SeedPresetAsync(options, "Coles");
        var supplierId = await SeedLinkedSupplierAsync(options, BusinessA, "Coles", "https://coles.test", presetId);

        await using var db = TestAppDbContext.For(options, BusinessA);
        var store = new EfSupplierStore(db);

        var updated = await store.UpdateAsync(
            supplierId,
            new SupplierFields("Coles", "Pat", "555", "pat@coles.test", "1 Road", "https://coles.test"),
            CancellationToken.None);

        var record = Updated(updated);
        Assert.Equal(("Pat", "555", "pat@coles.test", "1 Road"), (record.ContactName, record.Phone, record.Email, record.Address));
        Assert.Equal(presetId, record.SupplierPresetId);
    }

    /// <summary>
    /// "Not used" over the real schema: the linked copy leaves the selectable list, stays in the
    /// full one, is still found by id, and comes straight back when the switch is cleared.
    /// </summary>
    [Fact]
    public async Task Hiding_a_linked_supplier_takes_it_out_of_the_selectable_list_only()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        var presetId = await SeedPresetAsync(options, "Coles");
        var supplierId = await SeedLinkedSupplierAsync(options, BusinessA, "Coles", null, presetId);

        await using var db = TestAppDbContext.For(options, BusinessA);
        var store = new EfSupplierStore(db);
        await store.AddAsync(new SupplierFields("Acme", null, null, null, null, null), CancellationToken.None);

        Assert.Equal(SupplierWriteOutcome.Success, (await store.SetHiddenAsync(supplierId, true, CancellationToken.None)).Outcome);

        Assert.Equal(
            ["Acme", "Coles"],
            (await store.ListOrderedByNameAsync(CancellationToken.None)).Select(supplier => supplier.Name));
        Assert.Equal(
            ["Acme"],
            (await store.ListSelectableOrderedByNameAsync(CancellationToken.None)).Select(supplier => supplier.Name));
        Assert.True((await store.FindByIdAsync(supplierId, CancellationToken.None))!.IsHidden);

        Assert.Equal(SupplierWriteOutcome.Success, (await store.SetHiddenAsync(supplierId, false, CancellationToken.None)).Outcome);
        Assert.Equal(
            ["Acme", "Coles"],
            (await store.ListSelectableOrderedByNameAsync(CancellationToken.None)).Select(supplier => supplier.Name));
    }

    /// <summary>
    /// The link is to a platform row, and the key is <c>Restrict</c> (issue #934): a preset a
    /// business holds a copy of cannot be deleted out from under that copy. Presets are disabled
    /// rather than deleted, so this is the backstop rather than a path anything takes.
    /// </summary>
    [Fact]
    public async Task A_preset_a_business_holds_a_copy_of_cannot_be_deleted()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        var presetId = await SeedPresetAsync(options, "Coles");
        await SeedLinkedSupplierAsync(options, BusinessA, "Coles", null, presetId);

        await using var db = TestAppDbContext.Unrestricted(options);
        db.SupplierPresets.Remove(await db.SupplierPresets.SingleAsync(preset => preset.Id == presetId));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    /// <summary>
    /// One copy per preset per business, enforced by the filtered unique index rather than by a
    /// check in code - and per business, so two businesses each hold their own copy of the same
    /// preset.
    /// </summary>
    [Fact]
    public async Task A_business_may_hold_only_one_copy_of_a_preset_and_two_businesses_may_each_hold_one()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        var presetId = await SeedPresetAsync(options, "Coles");
        await SeedLinkedSupplierAsync(options, BusinessA, "Coles", null, presetId);
        await SeedLinkedSupplierAsync(options, BusinessB, "Coles", null, presetId);

        await using var db = TestAppDbContext.Unrestricted(options);
        db.Suppliers.Add(new Supplier { BusinessId = BusinessA, Name = "Coles", SupplierPresetId = presetId });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    /// <summary>
    /// The unique index is filtered, so the unlinked suppliers - the normal case - do not collide
    /// with each other.
    /// </summary>
    [Fact]
    public async Task Any_number_of_unlinked_suppliers_coexist()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await using var db = TestAppDbContext.For(options, BusinessA);
        var store = new EfSupplierStore(db);

        await store.AddAsync(new SupplierFields("Acme", null, null, null, null, null), CancellationToken.None);
        await store.AddAsync(new SupplierFields("Beta", null, null, null, null, null), CancellationToken.None);
        await store.AddAsync(new SupplierFields("Gamma", null, null, null, null, null), CancellationToken.None);

        Assert.Equal(3, (await store.ListOrderedByNameAsync(CancellationToken.None)).Count);
    }

    /// <summary>The record a successful update answers with, failing the test on any refusal.</summary>
    private static SupplierRecord Updated(SupplierWriteResult result)
    {
        Assert.Equal(SupplierWriteOutcome.Success, result.Outcome);
        return result.Supplier!;
    }

    private static async Task<int> SeedPresetAsync(DbContextOptions<AppDbContext> options, string name)
    {
        await using var db = TestAppDbContext.Unrestricted(options);
        var preset = new SupplierPreset
        {
            Name = name,
            NormalisedName = SupplierPresetPolicy.NormalisedName(name),
            CreatedAtUtc = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAtUtc = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.SupplierPresets.Add(preset);
        await db.SaveChangesAsync();
        return preset.Id;
    }

    /// <summary>
    /// Writes the row the approval's provisioning copy creates. It is seeded through the
    /// unrestricted context because no supplier use case links a row: a link is set when the copy is
    /// made, and never afterwards.
    /// </summary>
    private static async Task<int> SeedLinkedSupplierAsync(
        DbContextOptions<AppDbContext> options, int businessId, string name, string? url, int presetId)
    {
        await using var db = TestAppDbContext.Unrestricted(options);
        var supplier = new Supplier
        {
            BusinessId = businessId,
            Name = name,
            Url = url,
            SupplierPresetId = presetId,
        };
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync();
        return supplier.Id;
    }
}
