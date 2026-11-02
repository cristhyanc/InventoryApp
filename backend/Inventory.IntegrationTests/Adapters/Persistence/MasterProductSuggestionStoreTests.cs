using Inventory.Application.PriceCatalogue;
using Inventory.Domain.PriceCatalogue;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// The business lookup of the shared master products (issue #1049, parent #817) over the real
/// adapter, the real migration history and <strong>two</strong> businesses, on the business scope a
/// member's request actually carries.
///
/// <para>Two businesses throughout, as AGENTS.md § Tenant ownership requires of any change that
/// reads a platform table from a business endpoint: both of them have their own products and their
/// own tracking mappings here, so "the answer says nothing about any business, and no business's row
/// changes it" is asserted against a database where there <em>is</em> tenant data to leak. Relational
/// SQLite over the real migrations rather than InMemory, because what is under test is SQL - the
/// ordering, the case-insensitive <c>LIKE</c>, the escaped wildcard and the slice.</para>
///
/// <para>The <em>scoped</em> context is the shape that matters, and it is the opposite of the Super
/// Admin stores beside this one: this read is made by a member under their own resolved business, and
/// it must still answer the whole shared catalogue - because a master product carries no business key
/// and no central filter narrows it. The last test states that directly: a member's own scoped read
/// answers exactly what a completely unfiltered one does.</para>
/// </summary>
public class MasterProductSuggestionStoreTests
{
    /// <summary>
    /// The issue's own ordering: brand, then name, then size, then the row's own key - which is what
    /// makes paging over it stable, because two products that agree on the first three still come out
    /// in the same order on every read. The litre row sorts before the millilitre one because the
    /// value is what is compared.
    /// </summary>
    [Fact]
    public async Task The_matches_are_ordered_by_brand_then_name_then_size_then_key()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();

        var pepsi = await fixture.SeedMasterProductAsync("Pepsi Cola", "Max");
        var colaLarge = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic", 600m);
        var colaSmall = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic", 375m);
        var colaLitre = await fixture.SeedMasterProductAsync(
            "Coca-Cola", "Classic", 1.25m, MasterProductSizeUnit.Litre);
        var colaZero = await fixture.SeedMasterProductAsync("Coca-Cola", "Zero Sugar", 375m);

        var found = await SearchAsync(fixture, "cola");

        Assert.Equal(
            [colaLitre, colaSmall, colaLarge, colaZero, pepsi],
            found.Select(product => product.Id).ToArray());
    }

    /// <summary>
    /// The search matches the brand <em>or</em> the name, without regard to case: a person typing a
    /// brand and a person typing the product's own words find the same catalogue. Case folding is
    /// SQLite's <c>LIKE</c> rather than <c>instr</c>, which is what makes "COCA" find "Coca-Cola".
    /// </summary>
    [Theory]
    [InlineData("coca", 2)]
    [InlineData("COCA-COLA", 2)]
    [InlineData("zero", 1)]
    [InlineData("SUGAR", 1)]
    [InlineData("pepsi", 1)]
    [InlineData("nothing here", 0)]
    public async Task A_search_matches_the_brand_or_the_name_ignoring_case(string search, int matches)
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        await fixture.SeedMasterProductAsync("Coca-Cola", "Zero Sugar");
        await fixture.SeedMasterProductAsync("Pepsi", "Max");

        Assert.Equal(matches, (await SearchAsync(fixture, search)).Count);
    }

    /// <summary>
    /// A character <c>LIKE</c> treats specially is matched literally: a person typing <c>%</c> is
    /// searching for a product whose name holds a percent sign, not asking for the whole catalogue in
    /// one page.
    /// </summary>
    [Fact]
    public async Task A_wildcard_character_in_the_search_is_matched_literally()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var juice = await fixture.SeedMasterProductAsync("Golden Circle", "100% Orange Juice");
        await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");

        Assert.Equal([juice], (await SearchAsync(fixture, "100%")).Select(found => found.Id).ToArray());

        // A bare wildcard finds the one product whose name really holds a percent sign, and not the
        // cola beside it - which is what an unescaped pattern would have returned.
        Assert.Equal([juice], (await SearchAsync(fixture, "%")).Select(found => found.Id).ToArray());

        // And the single-character wildcard is literal too: no stored name holds "_o".
        Assert.Empty(await SearchAsync(fixture, "_o"));
    }

    /// <summary>
    /// The slice is the one it was given, and the rows are the ones that slice names - which is what
    /// the use case's "one more than the page" relies on to say whether more matches remain.
    /// </summary>
    [Fact]
    public async Task The_requested_slice_is_the_one_answered()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var first = await fixture.SeedMasterProductAsync("Cola Brand", "A");
        var second = await fixture.SeedMasterProductAsync("Cola Brand", "B");
        var third = await fixture.SeedMasterProductAsync("Cola Brand", "C");

        Assert.Equal(
            [first, second],
            (await SearchAsync(fixture, "cola", skip: 0, take: 2)).Select(found => found.Id).ToArray());
        Assert.Equal(
            [third],
            (await SearchAsync(fixture, "cola", skip: 2, take: 2)).Select(found => found.Id).ToArray());
        Assert.Empty(await SearchAsync(fixture, "cola", skip: 3, take: 2));
    }

    /// <summary>
    /// Each row carries the master product's own four values and its key, read from the catalogue
    /// rather than reconstructed: #1051 links by that key, and a person reads the brand, the name and
    /// the size.
    /// </summary>
    [Fact]
    public async Task A_match_carries_the_master_products_key_brand_name_and_size()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var id = await fixture.SeedMasterProductAsync(
            "CC's", "Tasty Cheese Corn Chips", 45m, MasterProductSizeUnit.Gram);

        var found = Assert.Single(await SearchAsync(fixture, "tasty cheese"));

        Assert.Equal(id, found.Id);
        Assert.Equal("CC's", found.Brand);
        Assert.Equal("Tasty Cheese Corn Chips", found.Name);
        Assert.Equal(45m, found.SizeValue);
        Assert.Equal(MasterProductSizeUnit.Gram, found.SizeUnit);
    }

    /// <summary>
    /// The decisive property: what a member reads under their own business scope is exactly what an
    /// unfiltered context reads, even while both businesses track products of their own. The lookup is
    /// not narrowed per business - a master product belongs to none - and the central filters are
    /// untouched, because this adapter reads no mapping, product or business at all.
    /// </summary>
    [Fact]
    public async Task A_members_own_scope_reads_the_same_catalogue_an_unfiltered_context_does()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        await fixture.SeedMasterProductAsync("Coca-Cola", "Zero Sugar");

        // Both businesses want a product priced, which is the tenant-owned half of this catalogue and
        // the only place a business appears in it at all.
        await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);
        await fixture.TrackAsync(fixture.SecondBusinessId, fixture.SecondBusinessColaId);

        var filter = new MasterProductSuggestionFilter("cola", Skip: 0, Take: 20);

        var firstBusiness = await fixture.SuggestionStoreFor(fixture.FirstBusinessId)
            .SearchAsync(filter, CancellationToken.None);
        var secondBusiness = await fixture.SuggestionStoreFor(fixture.SecondBusinessId)
            .SearchAsync(filter, CancellationToken.None);
        var unfiltered = await fixture.UnscopedSuggestionStore()
            .SearchAsync(filter, CancellationToken.None);

        Assert.Equal(unfiltered, firstBusiness);
        Assert.Equal(unfiltered, secondBusiness);
        Assert.NotEmpty(unfiltered);
    }

    /// <summary>
    /// And the issue's last criterion at the adapter: a lookup writes nothing. The master products
    /// carry the same instants afterwards, and neither business has gained or lost a mapping.
    /// </summary>
    [Fact]
    public async Task A_lookup_writes_nothing()
    {
        await using var fixture = await PriceCatalogueFixture.CreateAsync();
        var id = await fixture.SeedMasterProductAsync("Coca-Cola", "Classic");
        await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);

        var before = await fixture.MasterProductAsync(id);

        _ = await SearchAsync(fixture, "cola");

        var after = await fixture.MasterProductAsync(id);

        Assert.Equal(before!.Brand, after!.Brand);
        Assert.Equal(before.Name, after.Name);
        Assert.Equal(before.SizeValue, after.SizeValue);
        Assert.Equal(before.NormalisedKey, after.NormalisedKey);
        Assert.Equal(before.CreatedAtUtc, after.CreatedAtUtc);
        Assert.Equal(before.UpdatedAtUtc, after.UpdatedAtUtc);
        var mapping = Assert.Single(await fixture.AllTrackingsAsync());
        Assert.Equal(fixture.FirstBusinessColaId, mapping.ProductId);
    }

    private static async Task<IReadOnlyList<MasterProductIdentity>> SearchAsync(
        PriceCatalogueFixture fixture,
        string search,
        int skip = 0,
        int take = MasterProductSuggestionPaging.MaxPageSize) =>
        await fixture.SuggestionStoreFor(fixture.FirstBusinessId).SearchAsync(
            new MasterProductSuggestionFilter(search, skip, take),
            CancellationToken.None);
}
