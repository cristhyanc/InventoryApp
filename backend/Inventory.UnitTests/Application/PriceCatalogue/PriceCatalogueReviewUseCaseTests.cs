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
/// The use cases behind the Super Admin review surface (issue #928): the two things they decide
/// themselves, which are that nothing is decided for a request whose actor cannot be named and what
/// the audit trail records.
///
/// <para>Over a fake store, because everything else is a statement about a listing, a suggestion and
/// the mappings that carry a key - all of which has to be read in the transaction the decision is
/// written in, and is therefore <c>PriceCatalogueReviewTests</c>'. The rules themselves are
/// <c>SupplierListingReviewPolicyTests</c>' and <c>ProductMatchReviewPolicyTests</c>'.</para>
/// </summary>
public class PriceCatalogueReviewUseCaseTests
{
    private static readonly DateTime Now = new(2026, 10, 30, 9, 0, 0, DateTimeKind.Utc);

    private const string Tenant = "99999999-9999-9999-9999-999999999999";
    private const string ObjectId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";

    private static string WaitingKey => PriceCatalogueWaitingKey.For("Coca-Cola Classic", "375mL");

    /// <summary>
    /// A confirmation that accepted a price is one audit event, with the actor, the listing, the pack
    /// quantity confirmed and the clock's instant - and the decision carries the clock's instant into
    /// the store too, because freshness is an answer about now.
    /// </summary>
    [Fact]
    public async Task Confirming_a_listing_audits_the_decision_with_the_actor_and_the_instant()
    {
        var store = new FakeStore
        {
            ListingResult = new SupplierListingReviewResult(
                7,
                SupplierListingReviewOutcome.Confirmed,
                SupplierListingMatchStatus.Confirmed,
                PriceAccepted: true,
                PriceExceptionUsed: false,
                PriceReason: null,
                AcceptedObservationId: 11,
                AcceptedPackPrice: 42.50m,
                ConfirmedUnitsPerPack: 36),
        };
        var audit = new FakeAudit();

        var result = await Listing(store, audit).Handle(Confirm(), CancellationToken.None);

        Assert.True(result!.PriceAccepted);
        Assert.Equal(Now, store.AsOfUtc);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(PriceCatalogueReviewAction.ListingConfirmed, entry.Action);

        // The actor's own normalised pair, which is what every audit trail here records: ActorIdentity
        // upper-cases a GUID so two spellings of one identity are one comparison key.
        Assert.Equal(Tenant, entry.ActorDirectoryTenantId, ignoreCase: true);
        Assert.Equal(ObjectId, entry.ActorObjectId, ignoreCase: true);
        Assert.Equal(7, entry.SupplierListingId);
        Assert.Equal(36, entry.ConfirmedUnitsPerPack);
        Assert.True(entry.PriceAccepted);
        Assert.Equal(Now, entry.OccurredAtUtc);
    }

    /// <summary>
    /// The issue's own requirement: a price exception is audited <strong>separately</strong> from the
    /// listing decision, because it is a separate decision that overrides a financial safeguard. Two
    /// events, not one event with a flag.
    /// </summary>
    [Fact]
    public async Task An_accepted_price_exception_is_audited_separately_from_the_decision()
    {
        var store = new FakeStore
        {
            ListingResult = new SupplierListingReviewResult(
                7,
                SupplierListingReviewOutcome.Confirmed,
                SupplierListingMatchStatus.Confirmed,
                PriceAccepted: true,
                PriceExceptionUsed: true,
                PriceReason: null,
                AcceptedObservationId: 11,
                AcceptedPackPrice: 9.00m,
                ConfirmedUnitsPerPack: 30),
        };
        var audit = new FakeAudit();

        await Listing(store, audit).Handle(
            Confirm() with { ApprovePriceException = true },
            CancellationToken.None);

        Assert.Equal(
            [
                PriceCatalogueReviewAction.ListingConfirmed,
                PriceCatalogueReviewAction.PriceExceptionApproved,
            ],
            audit.Entries.Select(entry => entry.Action).ToArray());
    }

    /// <summary>
    /// An approval that overrode nothing is not an exception, so nothing is audited as one: the store
    /// reports whether an exception was <em>used</em>, and that is what the trail records.
    /// </summary>
    [Fact]
    public async Task An_approval_that_was_not_used_audits_no_exception()
    {
        var store = new FakeStore
        {
            ListingResult = new SupplierListingReviewResult(
                7,
                SupplierListingReviewOutcome.Confirmed,
                SupplierListingMatchStatus.Confirmed,
                PriceAccepted: true,
                PriceExceptionUsed: false,
                PriceReason: null,
                AcceptedObservationId: 11,
                AcceptedPackPrice: 42.50m,
                ConfirmedUnitsPerPack: 30),
        };
        var audit = new FakeAudit();

        await Listing(store, audit).Handle(
            Confirm() with { ApprovePriceException = true },
            CancellationToken.None);

        Assert.Equal(
            PriceCatalogueReviewAction.ListingConfirmed,
            Assert.Single(audit.Entries).Action);
    }

    /// <summary>A refusal is audited as a refusal, and no price was accepted by it.</summary>
    [Fact]
    public async Task Refusing_a_listing_audits_the_refusal()
    {
        var store = new FakeStore
        {
            ListingResult = new SupplierListingReviewResult(
                7,
                SupplierListingReviewOutcome.Rejected,
                SupplierListingMatchStatus.Rejected,
                PriceAccepted: false,
                PriceExceptionUsed: false,
                PriceReason: null,
                AcceptedObservationId: null,
                AcceptedPackPrice: null,
                ConfirmedUnitsPerPack: null),
        };
        var audit = new FakeAudit();

        await Listing(store, audit).Handle(
            Confirm() with { Outcome = SupplierListingReviewOutcome.Rejected },
            CancellationToken.None);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(PriceCatalogueReviewAction.ListingRejected, entry.Action);
        Assert.False(entry.PriceAccepted);
    }

    /// <summary>
    /// A listing nothing holds is answered with nothing - which the endpoint reports as <c>404</c> -
    /// and nothing is audited, because nothing happened.
    /// </summary>
    [Fact]
    public async Task An_unknown_listing_answers_nothing_and_audits_nothing()
    {
        var store = new FakeStore { ListingResult = null };
        var audit = new FakeAudit();

        Assert.Null(await Listing(store, audit).Handle(Confirm(), CancellationToken.None));
        Assert.Empty(audit.Entries);
    }

    /// <summary>
    /// An undeclared outcome is refused before the store is reached at all: an enum is a compile-time
    /// constraint and not a validation, and the status a decision maps to is the Domain's.
    /// </summary>
    [Fact]
    public async Task An_undeclared_listing_outcome_is_refused_before_the_store()
    {
        var store = new FakeStore();
        var audit = new FakeAudit();

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            Listing(store, audit).Handle(
                Confirm() with { Outcome = (SupplierListingReviewOutcome)7 },
                CancellationToken.None));

        Assert.Null(store.AsOfUtc);
        Assert.Empty(audit.Entries);
    }

    /// <summary>
    /// Nothing is decided for a request whose actor the trail could not name. The platform-admin
    /// policy has already established <em>that</em> this is the administrator; the actor is who.
    /// </summary>
    [Fact]
    public async Task A_request_with_no_identifiable_actor_decides_nothing()
    {
        var store = new FakeStore();
        var audit = new FakeAudit();

        await Assert.ThrowsAsync<BusinessAccessDeniedException>(() =>
            new DecideSupplierListingReview(
                store,
                FakeAuthenticatedActorAccessor.Unidentifiable(),
                audit,
                new FakeClock(Now))
                .Handle(Confirm(), CancellationToken.None));

        Assert.Null(store.AsOfUtc);
        Assert.Empty(audit.Entries);
    }

    /// <summary>
    /// A match that created a master product audits both facts, because this surface is the only
    /// writer of that table: "a master product came into existence" is its own event.
    /// </summary>
    [Fact]
    public async Task A_match_that_created_a_master_product_audits_both_facts()
    {
        var store = new FakeStore
        {
            MatchResult = new ProductMatchReviewResult(
                WaitingKey,
                ProductMatchReviewDecision.CreateMasterProduct,
                new ReviewedMasterProduct(5, "Coca-Cola", "Classic", 375m, MasterProductSizeUnit.Millilitre),
                MasterProductCreated: true,
                SuggestionId: 9,
                ListingsMovedCount: 2),
        };
        var audit = new FakeAudit();

        await Match(store, audit).Handle(
            new ProductMatchReviewSubmission(
                WaitingKey,
                ProductMatchReviewDecision.CreateMasterProduct,
                SuggestionId: null,
                MasterProductId: null,
                "Coca-Cola",
                "Classic",
                375m,
                MasterProductSizeUnit.Millilitre),
            CancellationToken.None);

        Assert.Equal(
            [
                PriceCatalogueReviewAction.MasterProductCreated,
                PriceCatalogueReviewAction.ProductMatched,
            ],
            audit.Entries.Select(entry => entry.Action).ToArray());

        Assert.All(audit.Entries, entry =>
        {
            Assert.Equal(WaitingKey, entry.WaitingKey);
            Assert.Equal(5, entry.MasterProductId);
            Assert.Equal(9, entry.SuggestionId);
            Assert.False(entry.PriceAccepted);
        });
    }

    /// <summary>Each of the other decisions audits itself as what it was.</summary>
    [Theory]
    [InlineData(ProductMatchReviewDecision.AcceptSuggestion, PriceCatalogueReviewAction.ProductMatched)]
    [InlineData(ProductMatchReviewDecision.ChooseMasterProduct, PriceCatalogueReviewAction.ProductMatched)]
    [InlineData(ProductMatchReviewDecision.RejectSuggestion, PriceCatalogueReviewAction.MatchSuggestionRejected)]
    [InlineData(ProductMatchReviewDecision.MarkNotFound, PriceCatalogueReviewAction.WaitingProductNotFound)]
    public async Task Each_match_decision_audits_itself(
        ProductMatchReviewDecision decision,
        PriceCatalogueReviewAction action)
    {
        var store = new FakeStore
        {
            MatchResult = new ProductMatchReviewResult(
                WaitingKey,
                decision,
                MasterProduct: null,
                MasterProductCreated: false,
                SuggestionId: 9,
                ListingsMovedCount: 0),
        };
        var audit = new FakeAudit();

        await Match(store, audit).Handle(
            new ProductMatchReviewSubmission(
                WaitingKey,
                decision,
                9,
                MasterProductId: null,
                NewBrand: null,
                NewName: null,
                NewSizeValue: null,
                NewSizeUnit: null),
            CancellationToken.None);

        Assert.Equal(action, Assert.Single(audit.Entries).Action);
    }

    /// <summary>An undeclared match decision is refused before the store is reached.</summary>
    [Fact]
    public async Task An_undeclared_match_decision_is_refused_before_the_store()
    {
        var store = new FakeStore();
        var audit = new FakeAudit();

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            Match(store, audit).Handle(
                new ProductMatchReviewSubmission(
                    WaitingKey,
                    (ProductMatchReviewDecision)9,
                    SuggestionId: null,
                    MasterProductId: null,
                    NewBrand: null,
                    NewName: null,
                    NewSizeValue: null,
                    NewSizeUnit: null),
                CancellationToken.None));

        Assert.Null(store.AsOfUtc);
        Assert.Empty(audit.Entries);
    }

    /// <summary>Both halves of the automatic-match decision are audited, and as different actions.</summary>
    [Theory]
    [InlineData(AutomaticMatchReviewDecision.Confirm, PriceCatalogueReviewAction.AutomaticMatchConfirmed)]
    [InlineData(AutomaticMatchReviewDecision.Undo, PriceCatalogueReviewAction.AutomaticMatchUndone)]
    public async Task Confirming_and_undoing_an_automatic_match_are_both_audited(
        AutomaticMatchReviewDecision decision,
        PriceCatalogueReviewAction action)
    {
        var store = new FakeStore
        {
            AutomaticResult = new AutomaticMatchReviewResult(
                9,
                decision,
                WaitingKey,
                new ReviewedMasterProduct(5, "Coca-Cola", "Classic", 375m, MasterProductSizeUnit.Millilitre),
                ListingsMovedCount: 1),
        };
        var audit = new FakeAudit();

        await Automatic(store, audit).Handle(9, decision, CancellationToken.None);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(action, entry.Action);
        Assert.Equal(9, entry.SuggestionId);
        Assert.Equal(5, entry.MasterProductId);
        Assert.Equal(WaitingKey, entry.WaitingKey);
    }

    private static SupplierListingReviewSubmission Confirm() =>
        new(
            7,
            SupplierListingReviewOutcome.Confirmed,
            UnitsPerPack: 30,
            Barcode: null,
            BarcodeScope: null,
            Variant: "Classic",
            SizeValue: 375m,
            MasterProductSizeUnit.Millilitre,
            ApprovePriceException: false);

    private static DecideSupplierListingReview Listing(FakeStore store, FakeAudit audit) =>
        new(store, Actor(), audit, new FakeClock(Now));

    private static DecideProductMatchReview Match(FakeStore store, FakeAudit audit) =>
        new(store, Actor(), audit, new FakeClock(Now));

    private static DecideAutomaticProductMatchReview Automatic(FakeStore store, FakeAudit audit) =>
        new(store, Actor(), audit, new FakeClock(Now));

    private static FakeAuthenticatedActorAccessor Actor()
    {
        Assert.True(ActorIdentity.TryCreate(Tenant, ObjectId, out var actor));

        return FakeAuthenticatedActorAccessor.Identified(actor!);
    }

    private sealed class FakeAudit : IPriceCatalogueReviewAudit
    {
        private readonly List<PriceCatalogueReviewAuditEntry> _entries = [];

        public IReadOnlyList<PriceCatalogueReviewAuditEntry> Entries => _entries;

        public void Record(PriceCatalogueReviewAuditEntry entry) => _entries.Add(entry);
    }

    private sealed class FakeStore : IPriceCatalogueReviewStore
    {
        public DateTime? AsOfUtc { get; private set; }

        public SupplierListingReviewResult? ListingResult { get; init; }

        public ProductMatchReviewResult? MatchResult { get; init; }

        public AutomaticMatchReviewResult? AutomaticResult { get; init; }

        public Task<PriceCatalogueReviewQueue> ReadQueueAsync(
            DateTime asOfUtc,
            CancellationToken cancellationToken)
        {
            AsOfUtc = asOfUtc;

            return Task.FromResult(new PriceCatalogueReviewQueue([], [], [], []));
        }

        public Task<SupplierListingReviewResult?> DecideListingAsync(
            SupplierListingReviewSubmission submission,
            DateTime asOfUtc,
            CancellationToken cancellationToken)
        {
            AsOfUtc = asOfUtc;

            return Task.FromResult(ListingResult);
        }

        public Task<ProductMatchReviewResult?> DecideMatchAsync(
            ProductMatchReviewSubmission submission,
            DateTime asOfUtc,
            CancellationToken cancellationToken)
        {
            AsOfUtc = asOfUtc;

            return Task.FromResult(MatchResult);
        }

        public Task<AutomaticMatchReviewResult?> DecideAutomaticMatchAsync(
            long suggestionId,
            AutomaticMatchReviewDecision decision,
            DateTime asOfUtc,
            CancellationToken cancellationToken)
        {
            AsOfUtc = asOfUtc;

            return Task.FromResult(AutomaticResult);
        }
    }
}
