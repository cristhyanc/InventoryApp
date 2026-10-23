using Inventory.Application.Suppliers;
using Xunit;

namespace InventoryApi.Tests.Application.Suppliers;

public class SupplierUseCaseTests
{
    [Fact]
    public async Task Create_then_list_returns_supplier_ordered_by_name()
    {
        var store = new FakeSupplierStore();
        await new CreateSupplier(store).Handle("Zebra Co", null, null, null, null, null, CancellationToken.None);
        await new CreateSupplier(store).Handle("Apple Co", "Contact", "555", "a@b.com", "1 Street", null, CancellationToken.None);

        var result = await new ListSuppliers(store).Handle(CancellationToken.None);

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
            created.Id, "s1-up", "cn2", "p2", "e2", "a2", null, CancellationToken.None);

        Assert.Equal(SupplierWriteOutcome.Success, updated.Outcome);
        var fetched = await new GetSupplier(store).Handle(created.Id, CancellationToken.None);
        Assert.Equal("s1-up", fetched!.Name);
    }

    [Fact]
    public async Task Update_missing_supplier_returns_not_found()
    {
        var store = new FakeSupplierStore();

        var updated = await new UpdateSupplier(store).Handle(
            999, "name", null, null, null, null, null, CancellationToken.None);

        Assert.Equal(SupplierWriteOutcome.NotFound, updated.Outcome);
        Assert.Null(updated.Supplier);
    }

    [Fact]
    public async Task Delete_existing_supplier_returns_true_and_removes_it()
    {
        var store = new FakeSupplierStore();
        var created = await CreateAsync(store, "s1", null, null, null, null, null);

        var deleted = await new DeleteSupplier(store).Handle(created.Id, CancellationToken.None);

        Assert.True(deleted);
        Assert.Null(await new GetSupplier(store).Handle(created.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Delete_missing_supplier_returns_false()
    {
        var store = new FakeSupplierStore();

        var deleted = await new DeleteSupplier(store).Handle(999, CancellationToken.None);

        Assert.False(deleted);
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

        var all = await new ListSuppliers(store).Handle(CancellationToken.None);
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
            created.Id, "Acme", null, null, null, null, "  ", CancellationToken.None);

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
            "Acme", null, null, null, null, submitted, CancellationToken.None);

        Assert.Equal(SupplierWriteOutcome.Invalid, result.Outcome);
        Assert.Equal(SupplierUrlPolicy.InvalidMessage, result.ValidationError);
        Assert.Null(result.Supplier);
        Assert.Empty(await new ListSuppliers(store).Handle(CancellationToken.None));
    }

    [Fact]
    public async Task A_refused_website_url_leaves_an_existing_supplier_untouched()
    {
        var store = new FakeSupplierStore();
        var created = await CreateAsync(store, "Acme", "Pat", "555", "pat@acme.test", "1 Road", "https://acme.test");

        var result = await new UpdateSupplier(store).Handle(
            created.Id, "Renamed", null, null, null, null, "javascript:alert(1)", CancellationToken.None);

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
            "Acme", null, null, null, null, tooLong, CancellationToken.None);

        Assert.Equal(SupplierWriteOutcome.Invalid, result.Outcome);
        Assert.Equal(SupplierUrlPolicy.TooLongMessage, result.ValidationError);
        Assert.Empty(await new ListSuppliers(store).Handle(CancellationToken.None));
    }

    private static async Task<SupplierRecord> CreateAsync(
        FakeSupplierStore store, string name, string? contactName, string? phone, string? email,
        string? address, string? url)
    {
        var result = await new CreateSupplier(store).Handle(
            name, contactName, phone, email, address, url, CancellationToken.None);

        Assert.Equal(SupplierWriteOutcome.Success, result.Outcome);
        return result.Supplier!;
    }
}
