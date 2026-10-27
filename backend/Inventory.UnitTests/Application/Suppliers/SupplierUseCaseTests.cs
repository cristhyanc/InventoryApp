using Inventory.Application.Suppliers;
using Inventory.Domain.Suppliers;
using Xunit;

namespace InventoryApi.Tests.Application.Suppliers;

public class SupplierUseCaseTests
{
    [Fact]
    public async Task Create_then_list_returns_supplier_ordered_by_name()
    {
        var store = new FakeSupplierStore();
        await CreateAsync(store, "Zebra Co", null, null, null, null, null);
        await CreateAsync(store, "Apple Co", "Contact", "555", "a@b.com", "1 Street", null);

        var result = await new ListSuppliers(store).Handle(includeHidden: true, CancellationToken.None);

        Assert.Equal(new[] { "Apple Co", "Zebra Co" }, result.Select(x => x.Name));
    }

    [Fact]
    public async Task Get_returns_null_for_missing_supplier()
    {
        var store = new FakeSupplierStore();

        var result = await new GetSupplier(store).Handle(999, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task Get_returns_existing_supplier()
    {
        var store = new FakeSupplierStore();
        var created = await CreateAsync(store, "s1", "cn", "p", "e", "a", null);

        var result = await new GetSupplier(store).Handle(created.Id, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("s1", result!.Name);
    }

    [Fact]
    public async Task Update_existing_supplier_returns_success_and_persists_changes()
    {
        var store = new FakeSupplierStore();
        var created = await CreateAsync(store, "s1", "cn", "p", "e", "a", null);

        var updated = await new UpdateSupplier(store).Handle(
            created.Id, new SupplierFields("s1-up", "cn2", "p2", "e2", "a2", null), CancellationToken.None);

        Assert.Equal(SupplierWriteOutcome.Success, updated.Outcome);
        var fetched = await new GetSupplier(store).Handle(created.Id, CancellationToken.None);
        Assert.Equal("s1-up", fetched!.Name);
    }

    [Fact]
    public async Task Update_missing_supplier_returns_not_found()
    {
        var store = new FakeSupplierStore();

        var updated = await new UpdateSupplier(store).Handle(
            999, new SupplierFields("name", null, null, null, null, null), CancellationToken.None);

        Assert.Equal(SupplierWriteOutcome.NotFound, updated.Outcome);
        Assert.Null(updated.Supplier);
    }

    [Fact]
    public async Task Delete_existing_supplier_returns_true_and_removes_it()
    {
        var store = new FakeSupplierStore();
        var created = await CreateAsync(store, "s1", null, null, null, null, null);

        var deleted = await new DeleteSupplier(store).Handle(created.Id, CancellationToken.None);

        Assert.Equal(SupplierWriteOutcome.Success, deleted.Outcome);
        Assert.Null(await new GetSupplier(store).Handle(created.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Delete_missing_supplier_reports_not_found()
    {
        var store = new FakeSupplierStore();

        var deleted = await new DeleteSupplier(store).Handle(999, CancellationToken.None);

        Assert.Equal(SupplierWriteOutcome.NotFound, deleted.Outcome);
    }

    /// <summary>
    /// Supplier name has no uniqueness constraint today (AppDbContext indexes but does not
    /// enforce uniqueness on Supplier.Name) - this locks in that existing, deliberately
    /// permissive behavior so a future change cannot silently start rejecting duplicates.
    /// </summary>
    [Fact]
    public async Task Creating_a_supplier_with_a_duplicate_name_is_allowed()
    {
        var store = new FakeSupplierStore();
        await CreateAsync(store, "Same Name", null, null, null, null, null);

        var second = await CreateAsync(store, "Same Name", null, null, null, null, null);

        var all = await new ListSuppliers(store).Handle(includeHidden: true, CancellationToken.None);
        Assert.Equal(2, all.Count);
        Assert.Equal("Same Name", second.Name);
    }

    /// <summary>
    /// The website URL (issue #847) goes through <see cref="SupplierUrlPolicy"/> on the way in, so
    /// what is stored is the trimmed value and a blank one is stored as "no website" rather than as
    /// an empty string.
    /// </summary>
    [Theory]
    [InlineData("  https://coles.com.au/trade  ", "https://coles.com.au/trade")]
    [InlineData("   ", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public async Task Create_normalizes_the_website_url(string? submitted, string? expected)
    {
        var store = new FakeSupplierStore();

        var created = await CreateAsync(store, "Acme", null, null, null, null, submitted);

        Assert.Equal(expected, created.Url);
        Assert.Equal(expected, (await new GetSupplier(store).Handle(created.Id, CancellationToken.None))!.Url);
    }

    [Fact]
    public async Task Update_normalizes_the_website_url_and_can_clear_it()
    {
        var store = new FakeSupplierStore();
        var created = await CreateAsync(store, "Acme", null, null, null, null, "https://coles.com.au");

        var cleared = await new UpdateSupplier(store).Handle(
            created.Id, new SupplierFields("Acme", null, null, null, null, "  "), CancellationToken.None);

        Assert.Equal(SupplierWriteOutcome.Success, cleared.Outcome);
        Assert.Null(cleared.Supplier!.Url);
    }

    /// <summary>
    /// A refused URL writes nothing at all: no supplier is created, and an existing supplier keeps
    /// every value it had, including the URL it already carried.
    /// </summary>
    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("coles.com.au")]
    [InlineData("ftp://files.example.test")]
    public async Task A_refused_website_url_creates_nothing(string submitted)
    {
        var store = new FakeSupplierStore();

        var result = await new CreateSupplier(store).Handle(
            new SupplierFields("Acme", null, null, null, null, submitted), CancellationToken.None);

        Assert.Equal(SupplierWriteOutcome.Invalid, result.Outcome);
        Assert.Equal(SupplierUrlPolicy.InvalidMessage, result.ValidationError);
        Assert.Null(result.Supplier);
        Assert.Empty(await new ListSuppliers(store).Handle(includeHidden: true, CancellationToken.None));
    }

    [Fact]
    public async Task A_refused_website_url_leaves_an_existing_supplier_untouched()
    {
        var store = new FakeSupplierStore();
        var created = await CreateAsync(store, "Acme", "Pat", "555", "pat@acme.test", "1 Road", "https://acme.test");

        var result = await new UpdateSupplier(store).Handle(
            created.Id,
            new SupplierFields("Renamed", null, null, null, null, "javascript:alert(1)"),
            CancellationToken.None);

        Assert.Equal(SupplierWriteOutcome.Invalid, result.Outcome);
        Assert.Equal(SupplierUrlPolicy.InvalidMessage, result.ValidationError);
        var unchanged = await new GetSupplier(store).Handle(created.Id, CancellationToken.None);
        Assert.Equal(
            ("Acme", "Pat", "555", "pat@acme.test", "1 Road", "https://acme.test"),
            (unchanged!.Name, unchanged.ContactName, unchanged.Phone, unchanged.Email, unchanged.Address, unchanged.Url));
    }

    /// <summary>
    /// An over-long URL is refused before the supplier is looked up, and with the length message
    /// rather than the scheme one.
    /// </summary>
    [Fact]
    public async Task An_over_long_website_url_is_refused_with_the_length_message()
    {
        var store = new FakeSupplierStore();
        var tooLong = "https://coles.com.au/" + new string('a', SupplierUrlPolicy.MaxLength);

        var result = await new CreateSupplier(store).Handle(
            new SupplierFields("Acme", null, null, null, null, tooLong), CancellationToken.None);

        Assert.Equal(SupplierWriteOutcome.Invalid, result.Outcome);
        Assert.Equal(SupplierUrlPolicy.TooLongMessage, result.ValidationError);
        Assert.Empty(await new ListSuppliers(store).Handle(includeHidden: true, CancellationToken.None));
    }

    /// <summary>
    /// A supplier the business created itself behaves exactly as it did before issue #934: it is
    /// unlinked, it is visible, and it is renamed and deleted freely.
    /// </summary>
    [Fact]
    public async Task A_created_supplier_is_unlinked_and_visible()
    {
        var store = new FakeSupplierStore();

        var created = await CreateAsync(store, "Acme", null, null, null, null, null);

        Assert.Null(created.SupplierPresetId);
        Assert.False(created.IsHidden);
    }

    /// <summary>
    /// The copy a business received keeps the master's name and website: a rename and a website
    /// change are both refused, and nothing is written.
    /// </summary>
    [Fact]
    public async Task A_linked_supplier_refuses_a_rename_and_a_website_change()
    {
        var store = new FakeSupplierStore();
        var linked = store.AddLinked("Coles", "https://coles.test", supplierPresetId: 3);

        var renamed = await new UpdateSupplier(store).Handle(
            linked.Id,
            new SupplierFields("Coles Group", "Pat", null, null, null, "https://coles.test"),
            CancellationToken.None);

        Assert.Equal(SupplierWriteOutcome.Invalid, renamed.Outcome);
        Assert.Equal(SupplierPresetLinkPolicy.RenameRefusedMessage, renamed.ValidationError);

        var rewebbed = await new UpdateSupplier(store).Handle(
            linked.Id,
            new SupplierFields("Coles", "Pat", null, null, null, "   "),
            CancellationToken.None);

        Assert.Equal(SupplierWriteOutcome.Invalid, rewebbed.Outcome);
        Assert.Equal(SupplierPresetLinkPolicy.WebsiteRefusedMessage, rewebbed.ValidationError);

        var unchanged = await new GetSupplier(store).Handle(linked.Id, CancellationToken.None);
        Assert.Equal(
            ("Coles", "https://coles.test", (string?)null),
            (unchanged!.Name, unchanged.Url, unchanged.ContactName));
    }

    /// <summary>
    /// What the owner's 27 October 2026 decision keeps editable on a copy: the contact details,
    /// submitted with the stored name and website exactly as the supplier form sends them.
    /// </summary>
    [Fact]
    public async Task A_linked_suppliers_contact_details_still_save()
    {
        var store = new FakeSupplierStore();
        var linked = store.AddLinked("Coles", "https://coles.test", supplierPresetId: 3);

        var updated = await new UpdateSupplier(store).Handle(
            linked.Id,
            new SupplierFields("Coles", "Pat", "555", "pat@coles.test", "1 Road", "https://coles.test"),
            CancellationToken.None);

        Assert.Equal(SupplierWriteOutcome.Success, updated.Outcome);
        Assert.Equal("Pat", updated.Supplier!.ContactName);
        Assert.Equal(3, updated.Supplier.SupplierPresetId);
    }

    [Fact]
    public async Task A_linked_supplier_cannot_be_deleted()
    {
        var store = new FakeSupplierStore();
        var linked = store.AddLinked("Coles", null, supplierPresetId: 3);

        var deleted = await new DeleteSupplier(store).Handle(linked.Id, CancellationToken.None);

        Assert.Equal(SupplierWriteOutcome.Invalid, deleted.Outcome);
        Assert.Equal(SupplierPresetLinkPolicy.DeleteRefusedMessage, deleted.ValidationError);
        Assert.NotNull(await new GetSupplier(store).Handle(linked.Id, CancellationToken.None));
    }

    /// <summary>
    /// "Not used" takes a linked copy out of the pickers and brings it back, and the list the
    /// Suppliers page reads keeps showing it throughout - otherwise it could never be switched back
    /// on.
    /// </summary>
    [Fact]
    public async Task Not_used_hides_a_linked_supplier_from_the_pickers_and_can_be_cleared()
    {
        var store = new FakeSupplierStore();
        var linked = store.AddLinked("Coles", null, supplierPresetId: 3);
        await CreateAsync(store, "Acme", null, null, null, null, null);

        var hidden = await new SetSupplierNotUsed(store).Handle(linked.Id, true, CancellationToken.None);

        Assert.Equal(SupplierWriteOutcome.Success, hidden.Outcome);
        Assert.Equal(
            ["Acme"],
            (await new ListSuppliers(store).Handle(includeHidden: false, CancellationToken.None))
                .Select(supplier => supplier.Name));
        Assert.Equal(
            ["Acme", "Coles"],
            (await new ListSuppliers(store).Handle(includeHidden: true, CancellationToken.None))
                .Select(supplier => supplier.Name));
        Assert.True((await new GetSupplier(store).Handle(linked.Id, CancellationToken.None))!.IsHidden);

        var shown = await new SetSupplierNotUsed(store).Handle(linked.Id, false, CancellationToken.None);

        Assert.Equal(SupplierWriteOutcome.Success, shown.Outcome);
        Assert.Equal(
            ["Acme", "Coles"],
            (await new ListSuppliers(store).Handle(includeHidden: false, CancellationToken.None))
                .Select(supplier => supplier.Name));
    }

    [Fact]
    public async Task Not_used_is_refused_for_a_supplier_the_business_created_itself()
    {
        var store = new FakeSupplierStore();
        var created = await CreateAsync(store, "Acme", null, null, null, null, null);

        var result = await new SetSupplierNotUsed(store).Handle(created.Id, true, CancellationToken.None);

        Assert.Equal(SupplierWriteOutcome.Invalid, result.Outcome);
        Assert.Equal(SupplierPresetLinkPolicy.HideRefusedMessage, result.ValidationError);
        Assert.False((await new GetSupplier(store).Handle(created.Id, CancellationToken.None))!.IsHidden);
    }

    [Fact]
    public async Task Not_used_reports_not_found_for_a_missing_supplier() =>
        Assert.Equal(
            SupplierWriteOutcome.NotFound,
            (await new SetSupplierNotUsed(new FakeSupplierStore()).Handle(999, true, CancellationToken.None)).Outcome);

    private static async Task<SupplierRecord> CreateAsync(
        FakeSupplierStore store, string name, string? contactName, string? phone, string? email,
        string? address, string? url)
    {
        var result = await new CreateSupplier(store).Handle(
            new SupplierFields(name, contactName, phone, email, address, url), CancellationToken.None);

        Assert.Equal(SupplierWriteOutcome.Success, result.Outcome);
        return result.Supplier!;
    }
}
