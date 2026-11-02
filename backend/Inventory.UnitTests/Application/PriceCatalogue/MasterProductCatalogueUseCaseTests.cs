using Inventory.Application.PriceCatalogue;
using Inventory.Application.Tenancy;
using Inventory.Domain.Exceptions;
using Inventory.Domain.PriceCatalogue;
using Inventory.Domain.Tenancy;
using InventoryApi.Tests.Application.Tenancy;
using InventoryApi.Tests.Application.Time;
using Xunit;

namespace Inventory.UnitTests.Application.PriceCatalogue;

/// <summary>
/// The use cases behind the Super Admin Master products page (issues #1023 and #1024): the few things
/// they decide themselves - which bounded page of the shared catalogue a request actually gets, what
/// reaches the port as a search term, and what a create or an edit refuses before a transaction is
/// ever opened.
///
/// <para>Over a fake store, because everything else - the ordering, the case-insensitive match, the
/// listing counts, the duplicate key, the confirmed-listing safeguard and the stale-edit comparison -
/// is a statement about SQL and about state read inside a write transaction, and is therefore
/// <c>MasterProductCatalogueStoreTests</c>' and <c>MasterProductCatalogueWriteStoreTests</c>'. What is
/// asserted here is that the page size is the server's answer and not the client's, that the page
/// actually served is the one reported back, that a blank search narrows nothing, and - for the two
/// writes - that nothing is written for a request whose actor cannot be named, that the description
/// reaching the port is the Domain's normalised one, and what the audit trail records.</para>
/// </summary>
public class MasterProductCatalogueUseCaseTests
{
    private static readonly DateTime CreatedAt = new(2026, 10, 30, 6, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// A request that asks for nothing gets the first page at the default size, and the port is asked
    /// for exactly that slice.
    /// </summary>
    [Fact]
    public async Task A_request_with_no_paging_reads_the_first_default_sized_page()
    {
        var store = new FakeStore();

        var page = await new ListMasterProducts(store).Handle(
            new MasterProductCatalogueQuery(),
            CancellationToken.None);

        Assert.Equal(0, store.Filter!.Skip);
        Assert.Equal(MasterProductCataloguePaging.DefaultPageSize, store.Filter.Take);
        Assert.Equal(1, page.Page);
        Assert.Equal(MasterProductCataloguePaging.DefaultPageSize, page.PageSize);
    }

    /// <summary>
    /// The bound the issue states: a page size above the maximum is served at the maximum rather than
    /// refused, and the response says which size it was - so a client cannot ask this surface for the
    /// whole catalogue in one response, and is not left guessing what it received.
    /// </summary>
    [Theory]
    [InlineData(201, MasterProductCataloguePaging.MaxPageSize)]
    [InlineData(10_000, MasterProductCataloguePaging.MaxPageSize)]
    [InlineData(200, 200)]
    [InlineData(25, 25)]
    public async Task A_requested_page_size_is_bounded_and_the_served_size_is_reported(
        int requested,
        int served)
    {
        var store = new FakeStore();

        var page = await new ListMasterProducts(store).Handle(
            new MasterProductCatalogueQuery(PageSize: requested),
            CancellationToken.None);

        Assert.Equal(served, store.Filter!.Take);
        Assert.Equal(served, page.PageSize);
    }

    /// <summary>
    /// A page number or size that is not a positive number is the absence of one: zero and negative
    /// values take the first page at the default size rather than a negative skip, which would be a
    /// refusal the page could do nothing with - and an empty slice the database would have had to
    /// answer.
    /// </summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(-3, -7)]
    public async Task A_page_or_size_that_is_not_positive_falls_back_to_the_first_default_page(
        int page,
        int pageSize)
    {
        var store = new FakeStore();

        var result = await new ListMasterProducts(store).Handle(
            new MasterProductCatalogueQuery(Page: page, PageSize: pageSize),
            CancellationToken.None);

        Assert.Equal(0, store.Filter!.Skip);
        Assert.Equal(MasterProductCataloguePaging.DefaultPageSize, store.Filter.Take);
        Assert.Equal(1, result.Page);
    }

    /// <summary>The third page of twenty is a skip of forty, which is what server-side paging means.</summary>
    [Fact]
    public async Task A_later_page_is_read_as_a_skip_of_the_pages_before_it()
    {
        var store = new FakeStore();

        await new ListMasterProducts(store).Handle(
            new MasterProductCatalogueQuery(Page: 3, PageSize: 20),
            CancellationToken.None);

        Assert.Equal(40, store.Filter!.Skip);
        Assert.Equal(20, store.Filter.Take);
    }

    /// <summary>
    /// The total is the number of products the search matches, not the number returned, and whether
    /// anything is left after this page is derived from that - so a page of fifty out of two hundred
    /// knows it is not the end.
    /// </summary>
    [Fact]
    public async Task The_total_is_the_match_count_and_decides_whether_more_remain()
    {
        var store = new FakeStore
        {
            Result = new MasterProductCatalogueResult([Product(1, "Coca-Cola")], TotalCount: 200),
        };

        var page = await new ListMasterProducts(store).Handle(
            new MasterProductCatalogueQuery(Page: 1, PageSize: 50),
            CancellationToken.None);

        Assert.Equal(200, page.TotalCount);
        Assert.Single(page.Products);
        Assert.True(page.HasMore);

        var last = new MasterProductCataloguePage(page.Products, 200, Page: 4, PageSize: 50);
        Assert.False(last.HasMore);
    }

    /// <summary>
    /// A search term is trimmed and a blank one is dropped: a search box somebody cleared must narrow
    /// the catalogue to nothing at all rather than to the products whose brand contains a space.
    /// </summary>
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(" cola ", "cola")]
    [InlineData("Coca-Cola", "Coca-Cola")]
    public async Task A_blank_search_narrows_nothing_and_a_typed_one_is_trimmed(
        string? typed,
        string? expected)
    {
        var store = new FakeStore();

        await new ListMasterProducts(store).Handle(
            new MasterProductCatalogueQuery(Search: typed),
            CancellationToken.None);

        Assert.Equal(expected, store.Filter!.Search);
    }

    /// <summary>
    /// The detail read is the port's answer, unchanged: an id no master product has is
    /// <see langword="null"/>, which the endpoint reports as <c>404</c> rather than as an empty
    /// product.
    /// </summary>
    [Fact]
    public async Task An_unknown_master_product_is_not_found()
    {
        var store = new FakeStore();

        Assert.Null(await new GetMasterProduct(store).Handle(404, CancellationToken.None));
        Assert.Equal(404, store.RequestedId);
    }

    /// <summary>A known master product comes back with the listings the port answered.</summary>
    [Fact]
    public async Task A_known_master_product_comes_back_with_its_listings()
    {
        var store = new FakeStore
        {
            Detail = new MasterProductDetail(
                Product(7, "Coca-Cola"),
                [
                    new CataloguedSupplierListing(
                        11,
                        "Alpha Grocer",
                        "SKU-COLA-30",
                        "Alpha Grocer Coca-Cola Classic 375mL 30 pack",
                        375m,
                        MasterProductSizeUnit.Millilitre,
                        30,
                        SupplierListingMatchStatus.Confirmed,
                        "https://alpha.example/cola",
                        CreatedAt,
                        new CataloguedObservedPrice(
                            42.50m,
                            "AUD",
                            SupplierPriceBasis.InclusiveOfGst,
                            CreatedAt)),
                ]),
        };

        var detail = await new GetMasterProduct(store).Handle(7, CancellationToken.None);

        Assert.Equal(7, detail!.Product.Id);
        var listing = Assert.Single(detail.Listings);
        Assert.Equal("Alpha Grocer", listing.SupplierName);
        Assert.Equal(42.50m, listing.LatestPrice!.PackPrice);
    }

    /// <summary>
    /// The counts are counts of supplier listings in each match status, and the total is their sum -
    /// including the refused ones, which are kept rather than deleted.
    /// </summary>
    [Fact]
    public void The_listing_counts_total_every_status_including_the_refused_ones()
    {
        var counts = new MasterProductListingCounts(Proposed: 2, Confirmed: 3, NeedsReview: 1, Rejected: 4);

        Assert.Equal(10, counts.Total);
    }

    #region Creating one

    /// <summary>
    /// A create hands the port the Domain's own normalised description - trimmed values and the key
    /// computed from all four at once - so the row the catalogue stores and the key it is compared by
    /// cannot come from two different places.
    /// </summary>
    [Fact]
    public async Task A_create_hands_the_port_the_normalised_description_and_the_clock_instant()
    {
        var writes = new FakeWriteStore();
        var audit = new FakeAudit();

        var created = await Create(writes, audit).Handle(
            new MasterProductSubmission("  Coca-Cola  ", " Classic ", 375.0m, MasterProductSizeUnit.Millilitre),
            CancellationToken.None);

        Assert.Equal("Coca-Cola", writes.Created!.Brand);
        Assert.Equal("Classic", writes.Created.Name);
        Assert.Equal(375.0m, writes.Created.SizeValue);
        Assert.Equal(
            MasterProductPolicy.NormalisedKey("Coca-Cola", "Classic", 375m, MasterProductSizeUnit.Millilitre),
            writes.Created.NormalisedKey);
        Assert.Equal(Now, writes.AsOfUtc);
        Assert.Equal(FakeWriteStore.CreatedId, created.Id);
    }

    /// <summary>
    /// The create is audited with the actor, the row that came into existence and the clock's
    /// instant - one event, on the same trail the review's own create writes to, because it is the
    /// same shared catalogue.
    /// </summary>
    [Fact]
    public async Task A_create_is_audited_with_the_actor_and_the_product()
    {
        var writes = new FakeWriteStore();
        var audit = new FakeAudit();

        await Create(writes, audit).Handle(Submission(), CancellationToken.None);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(PriceCatalogueReviewAction.MasterProductCreated, entry.Action);
        Assert.Equal(Tenant, entry.ActorDirectoryTenantId, ignoreCase: true);
        Assert.Equal(ObjectId, entry.ActorObjectId, ignoreCase: true);
        Assert.Equal(FakeWriteStore.CreatedId, entry.MasterProductId);
        Assert.Equal(Now, entry.OccurredAtUtc);

        // A catalogue change decides no listing, no suggestion and no key, so the fields a review
        // decision fills are empty rather than invented.
        Assert.Null(entry.SupplierListingId);
        Assert.Null(entry.SuggestionId);
        Assert.Null(entry.WaitingKey);
        Assert.False(entry.PriceAccepted);
        Assert.Null(entry.ConfirmedUnitsPerPack);
    }

    /// <summary>
    /// A description this catalogue cannot store is refused by the Domain before the port is reached
    /// at all: nothing is written, and no transaction is opened to find that out.
    /// </summary>
    [Theory]
    [InlineData("", "Classic", 375, MasterProductSizeUnit.Millilitre)]
    [InlineData("   ", "Classic", 375, MasterProductSizeUnit.Millilitre)]
    [InlineData("Coca-Cola", "", 375, MasterProductSizeUnit.Millilitre)]
    [InlineData("Coca-Cola", "Classic", 0, MasterProductSizeUnit.Millilitre)]
    [InlineData("Coca-Cola", "Classic", -375, MasterProductSizeUnit.Millilitre)]
    [InlineData("Coca-Cola", "Classic", 375.0001, MasterProductSizeUnit.Millilitre)]
    [InlineData("Coca-Cola", "Classic", 375, (MasterProductSizeUnit)99)]
    public async Task An_invalid_description_is_refused_before_the_port_is_reached(
        string brand,
        string name,
        decimal size,
        MasterProductSizeUnit unit)
    {
        var writes = new FakeWriteStore();
        var audit = new FakeAudit();

        await Assert.ThrowsAsync<DomainValidationException>(
            () => Create(writes, audit).Handle(
                new MasterProductSubmission(brand, name, size, unit),
                CancellationToken.None));

        Assert.Null(writes.Created);
        Assert.Empty(audit.Entries);
    }

    /// <summary>
    /// Nothing is written for a request whose actor the audit trail could not name, and the refusal
    /// happens before the port is reached: a change to the shared catalogue that nobody can be
    /// attributed to is not one this surface makes.
    /// </summary>
    [Fact]
    public async Task A_create_by_an_unidentifiable_actor_writes_nothing()
    {
        var writes = new FakeWriteStore();
        var audit = new FakeAudit();

        await Assert.ThrowsAsync<BusinessAccessDeniedException>(
            () => new CreateMasterProduct(
                    writes,
                    FakeAuthenticatedActorAccessor.NotAuthenticated(),
                    audit,
                    new FakeClock(Now))
                .Handle(Submission(), CancellationToken.None));

        Assert.Null(writes.Created);
        Assert.Empty(audit.Entries);
    }

    #endregion

    #region Editing one

    /// <summary>
    /// An edit hands the port the normalised description, the instant the page read and the clock's
    /// instant - and the row's own id, which is what makes a correction an update of the row every
    /// business is already pointing at rather than a new one.
    /// </summary>
    [Fact]
    public async Task An_edit_hands_the_port_the_description_the_expected_instant_and_the_id()
    {
        var writes = new FakeWriteStore { Updated = Product(7, "Coca-Cola") };
        var audit = new FakeAudit();

        var updated = await Update(writes, audit).Handle(
            7,
            new MasterProductEditSubmission(
                " Coca-Cola ",
                " Zero Sugar ",
                600m,
                MasterProductSizeUnit.Millilitre,
                CreatedAt),
            CancellationToken.None);

        Assert.Equal(7, writes.UpdatedId);
        Assert.Equal("Zero Sugar", writes.Edit!.Description.Name);
        Assert.Equal(600m, writes.Edit.Description.SizeValue);
        Assert.Equal(CreatedAt, writes.Edit.ExpectedUpdatedAtUtc);
        Assert.Equal(Now, writes.AsOfUtc);
        Assert.Equal(7, updated!.Id);
    }

    /// <summary>An accepted edit is one audit event of its own, distinct from a create.</summary>
    [Fact]
    public async Task An_edit_is_audited_as_an_edit()
    {
        var writes = new FakeWriteStore { Updated = Product(7, "Coca-Cola") };
        var audit = new FakeAudit();

        await Update(writes, audit).Handle(7, EditSubmission(), CancellationToken.None);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(PriceCatalogueReviewAction.MasterProductEdited, entry.Action);
        Assert.Equal(ObjectId, entry.ActorObjectId, ignoreCase: true);
        Assert.Equal(7, entry.MasterProductId);
        Assert.Equal(Now, entry.OccurredAtUtc);
    }

    /// <summary>
    /// An id no master product has is <see langword="null"/> - the endpoint's <c>404</c> - and
    /// <strong>nothing is audited</strong>: an audit trail records what happened, and nothing did.
    /// </summary>
    [Fact]
    public async Task An_edit_of_an_unknown_product_is_not_found_and_is_not_audited()
    {
        var writes = new FakeWriteStore { Updated = null };
        var audit = new FakeAudit();

        Assert.Null(await Update(writes, audit).Handle(404, EditSubmission(), CancellationToken.None));
        Assert.Empty(audit.Entries);
    }

    /// <summary>
    /// An edit that carries no expected instant is refused as an incomplete request, before the port
    /// is reached: a write that presents nothing to compare against is how a concurrent correction
    /// gets silently overwritten, and the refusal is a <c>400</c> rather than the <c>409</c> a stale
    /// one gets, because the page must send the field rather than re-read.
    /// </summary>
    [Fact]
    public async Task An_edit_with_no_expected_instant_is_refused_before_the_port_is_reached()
    {
        var writes = new FakeWriteStore { Updated = Product(7, "Coca-Cola") };
        var audit = new FakeAudit();

        var refusal = await Assert.ThrowsAsync<DomainValidationException>(
            () => Update(writes, audit).Handle(
                7,
                EditSubmission() with { ExpectedUpdatedAtUtc = null },
                CancellationToken.None));

        Assert.Equal(MasterProductEditPolicy.ExpectedUpdatedAtRequiredMessage, refusal.Message);
        Assert.Null(writes.Edit);
        Assert.Empty(audit.Entries);
    }

    /// <summary>An invalid description is refused before the port here too.</summary>
    [Fact]
    public async Task An_edit_to_an_invalid_description_is_refused_before_the_port_is_reached()
    {
        var writes = new FakeWriteStore { Updated = Product(7, "Coca-Cola") };
        var audit = new FakeAudit();

        await Assert.ThrowsAsync<DomainValidationException>(
            () => Update(writes, audit).Handle(
                7,
                EditSubmission() with { Brand = "   " },
                CancellationToken.None));

        Assert.Null(writes.Edit);
        Assert.Empty(audit.Entries);
    }

    /// <summary>Nothing is written for an edit whose actor cannot be named either.</summary>
    [Fact]
    public async Task An_edit_by_an_unidentifiable_actor_writes_nothing()
    {
        var writes = new FakeWriteStore { Updated = Product(7, "Coca-Cola") };
        var audit = new FakeAudit();

        await Assert.ThrowsAsync<BusinessAccessDeniedException>(
            () => new UpdateMasterProduct(
                    writes,
                    FakeAuthenticatedActorAccessor.NotAuthenticated(),
                    audit,
                    new FakeClock(Now))
                .Handle(7, EditSubmission(), CancellationToken.None));

        Assert.Null(writes.Edit);
        Assert.Empty(audit.Entries);
    }

    #endregion

    private const string Tenant = "99999999-9999-9999-9999-999999999999";
    private const string ObjectId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";

    private static readonly DateTime Now = new(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc);

    private static MasterProductSubmission Submission() =>
        new("Coca-Cola", "Classic", 375m, MasterProductSizeUnit.Millilitre);

    private static MasterProductEditSubmission EditSubmission() =>
        new("Coca-Cola", "Classic", 375m, MasterProductSizeUnit.Millilitre, CreatedAt);

    private static CreateMasterProduct Create(FakeWriteStore store, FakeAudit audit) =>
        new(store, Actor(), audit, new FakeClock(Now));

    private static UpdateMasterProduct Update(FakeWriteStore store, FakeAudit audit) =>
        new(store, Actor(), audit, new FakeClock(Now));

    private static FakeAuthenticatedActorAccessor Actor()
    {
        Assert.True(ActorIdentity.TryCreate(Tenant, ObjectId, out var actor));

        return FakeAuthenticatedActorAccessor.Identified(actor!);
    }

    private static CataloguedMasterProduct Product(long id, string brand) =>
        new(
            id,
            brand,
            "Classic",
            375m,
            MasterProductSizeUnit.Millilitre,
            CreatedAt,
            CreatedAt,
            new MasterProductListingCounts(0, 0, 0, 0));

    private sealed class FakeAudit : IPriceCatalogueReviewAudit
    {
        private readonly List<PriceCatalogueReviewAuditEntry> _entries = [];

        public IReadOnlyList<PriceCatalogueReviewAuditEntry> Entries => _entries;

        public void Record(PriceCatalogueReviewAuditEntry entry) => _entries.Add(entry);
    }

    private sealed class FakeWriteStore : IMasterProductCatalogueWriteStore
    {
        internal const long CreatedId = 42;

        public NormalisedMasterProduct? Created { get; private set; }

        public long? UpdatedId { get; private set; }

        public MasterProductEdit? Edit { get; private set; }

        public DateTime? AsOfUtc { get; private set; }

        public CataloguedMasterProduct? Updated { get; init; }

        public Task<CataloguedMasterProduct> CreateAsync(
            NormalisedMasterProduct description,
            DateTime asOfUtc,
            CancellationToken cancellationToken)
        {
            Created = description;
            AsOfUtc = asOfUtc;

            return Task.FromResult(new CataloguedMasterProduct(
                CreatedId,
                description.Brand,
                description.Name,
                description.SizeValue,
                description.SizeUnit,
                asOfUtc,
                asOfUtc,
                new MasterProductListingCounts(0, 0, 0, 0)));
        }

        public Task<CataloguedMasterProduct?> UpdateAsync(
            long masterProductId,
            MasterProductEdit edit,
            DateTime asOfUtc,
            CancellationToken cancellationToken)
        {
            UpdatedId = masterProductId;
            Edit = edit;
            AsOfUtc = asOfUtc;

            return Task.FromResult(Updated);
        }
    }

    private sealed class FakeStore : IMasterProductCatalogueStore
    {
        public MasterProductCatalogueFilter? Filter { get; private set; }

        public long? RequestedId { get; private set; }

        public MasterProductCatalogueResult Result { get; init; } = new([], 0);

        public MasterProductDetail? Detail { get; init; }

        public Task<MasterProductCatalogueResult> ListAsync(
            MasterProductCatalogueFilter filter,
            CancellationToken cancellationToken)
        {
            Filter = filter;
            return Task.FromResult(Result);
        }

        public Task<MasterProductDetail?> FindAsync(long masterProductId, CancellationToken cancellationToken)
        {
            RequestedId = masterProductId;
            return Task.FromResult(Detail);
        }
    }
}
