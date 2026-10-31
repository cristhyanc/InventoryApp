using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Inventory.Domain.PriceCatalogue;
using Inventory.Domain.Suppliers;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// The Super Admin price-catalogue review API over real HTTP (issue #928, parent #817): who may reach
/// it, what it reports, what each decision stores, what it refuses - and the decisive assertion that
/// nothing about any business appears in any of it.
///
/// <para>Everything here goes through the complete pipeline - authentication, <c>[Authorize]</c>, the
/// platform-admin policy, <c>[RequiredScope]</c>, <c>RolePreviewMiddleware</c>,
/// <c>BusinessScopeMiddleware</c>'s independent re-check of the same policy, the tenant query filters
/// and <c>BusinessOwnershipEnforcer</c> - so the authorization under test is the real one. Only the
/// authentication is synthetic.</para>
/// </summary>
public class SuperAdminPriceCatalogueReviewApiTests
    : IClassFixture<SuperAdminBusinessesApiFactory>, IAsyncLifetime
{
    private const string ReviewUrl = "/api/super-admin/price-catalogue-review";

    // Relative to now: the review only accepts a reading inside the supplier's freshness window
    // (30 hours), so a fixed date made these tests fail from 31 October 2026 12:00 UTC.
    private static readonly DateTime Observed = DateTime.UtcNow.AddHours(-1);

    private readonly SuperAdminBusinessesApiFactory _factory;

    public SuperAdminPriceCatalogueReviewApiTests(SuperAdminBusinessesApiFactory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        // The catalogue first: a supplier listing's own supplier master record is restricted on
        // delete, so the presets cannot be reset while a listing names one.
        await _factory.ResetPriceCatalogueAsync();
        await _factory.ResetAsync();
        await _factory.ResetSupplierPresetsAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    #region Who may reach it

    /// <summary>
    /// The issue's own criterion, asked of every endpoint: members, Owners included, are refused.
    /// An Owner holds the strongest business role there is and reaches none of this, because a
    /// supplier listing is shared by every business and a business role is not platform authority.
    /// </summary>
    [Theory]
    [InlineData(SuperAdminBusinessesApiFactory.TradingOwnerObjectId)]
    [InlineData(SuperAdminBusinessesApiFactory.TradingManagerObjectId)]
    [InlineData(SuperAdminBusinessesApiFactory.ApplicantObjectId)]
    [InlineData(SuperAdminBusinessesApiFactory.StrangerObjectId)]
    public async Task Every_endpoint_refuses_a_caller_who_is_not_the_configured_administrator(
        string objectId)
    {
        var client = _factory.Authenticated(objectId);

        foreach (var response in await AllEndpointsAsync(client))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    /// <summary>
    /// An anonymous request is refused by authentication with <c>401</c>, not turned into a
    /// <c>403</c>: "you are not signed in" and "you are signed in and not the administrator" stay
    /// different answers.
    /// </summary>
    [Fact]
    public async Task An_unauthenticated_request_is_unauthorized()
    {
        var client = _factory.CreateClient();

        foreach (var response in await AllEndpointsAsync(client))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    /// <summary>
    /// The delegated scope is required of the administrator too: platform authority is an additional
    /// requirement on a delegated API call, never a replacement for one.
    /// </summary>
    [Fact]
    public async Task The_administrator_without_the_delegated_scope_is_refused()
    {
        var client = _factory.WithoutScope(SuperAdminBusinessesApiFactory.AdministratorObjectId);

        foreach (var response in await AllEndpointsAsync(client))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    /// <summary>
    /// A member who presents a support-session id reaches nothing here either, and the refusal is the
    /// platform-admin one rather than anything about the session: this is a
    /// <c>PlatformDiagnosticsEndpointAttribute</c> endpoint, so the <c>X-Support-Session</c> header is
    /// never acted on at all. A member who sets the header is still an ordinary member.
    /// </summary>
    [Fact]
    public async Task A_member_presenting_a_support_session_id_is_still_refused()
    {
        const string SessionId = "a-seeded-support-session-id-for-the-review";
        await _factory.SeedSupportSessionAsync(
            _factory.TradingBusinessId,
            SuperAdminBusinessesApiFactory.AdministratorObjectId,
            SessionId);

        var client = _factory.Authenticated(SuperAdminBusinessesApiFactory.TradingOwnerObjectId);
        client.DefaultRequestHeaders.Add("X-Support-Session", SessionId);

        foreach (var response in await AllEndpointsAsync(client))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    /// <summary>
    /// A role preview is read-only everywhere, this surface included (issue #720): every write is
    /// refused with the preview's own stable code before the endpoint runs, and nothing is stored.
    /// </summary>
    [Theory]
    [InlineData("Owner")]
    [InlineData("Manager")]
    [InlineData("Operator")]
    public async Task A_role_preview_write_is_refused_as_read_only_and_stores_nothing(string role)
    {
        var arranged = await ArrangeAsync();

        var client = _factory.AsAdministrator();
        client.DefaultRequestHeaders.Add("X-Role-Preview", role);

        foreach (var response in await AllWritesAsync(client, arranged))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("read_only_role_preview", await CodeAsync(response));
        }

        var listing = await ListingAsync(arranged.ListingId);
        Assert.Equal(SupplierListingMatchStatus.Proposed, listing.MatchStatus);
        Assert.Empty(await ObservationsAsync());
    }

    /// <summary>
    /// Reaching this surface grants nothing else: the administrator is a member of no business, so
    /// every business endpoint still refuses them.
    /// </summary>
    [Fact]
    public async Task The_administrator_still_reaches_no_business_endpoint()
    {
        var client = _factory.AsAdministrator();

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/products")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/suppliers")).StatusCode);
    }

    #endregion

    #region The queue

    /// <summary>
    /// The first and second criteria over the wire: two businesses waiting under one key are one
    /// entry with a count of two, carrying the matcher's suggestion, the listings a search found and
    /// the reading one of them is holding.
    ///
    /// <para><strong>And the decisive assertion</strong>: neither business's name, nor either
    /// business's id, nor either product's id appears anywhere in the body. That is what makes "two
    /// businesses on one key are matched together without seeing each other" true by construction
    /// rather than by filtering - there is nowhere in this contract for any of it to travel.</para>
    /// </summary>
    [Fact]
    public async Task The_queue_reports_a_waiting_product_once_and_names_no_business()
    {
        var arranged = await ArrangeAsync();

        var response = await _factory.AsAdministrator().GetAsync(ReviewUrl);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        var queue = JsonDocument.Parse(body).RootElement;

        var waiting = Assert.Single(queue.GetProperty("waitingProducts").EnumerateArray());
        Assert.Equal("Coca-Cola Classic", waiting.GetProperty("searchName").GetString());
        Assert.Equal("375mL", waiting.GetProperty("unitLabel").GetString());
        Assert.Equal(2, waiting.GetProperty("businessesWaitingCount").GetInt32());

        var suggestion = Assert.Single(waiting.GetProperty("suggestions").EnumerateArray());
        Assert.Equal(arranged.SuggestionId, suggestion.GetProperty("id").GetInt64());
        Assert.Equal(
            "Coca-Cola",
            suggestion.GetProperty("masterProduct").GetProperty("brand").GetString());

        var listing = Assert.Single(queue.GetProperty("listings").EnumerateArray());
        Assert.Equal(arranged.ListingId, listing.GetProperty("id").GetInt64());
        Assert.Equal("Alpha Grocer", listing.GetProperty("supplierName").GetString());
        Assert.Equal(42.50m, listing.GetProperty("pendingObservation").GetProperty("packPrice").GetDecimal());
        Assert.True(listing.GetProperty("pendingObservation").GetProperty("isFresh").GetBoolean());

        Assert.DoesNotContain(SuperAdminBusinessesApiFactory.TradingBusinessName, body, StringComparison.Ordinal);
        Assert.DoesNotContain(SecondBusinessName, body, StringComparison.Ordinal);

        // Asserted as JSON property names - the leading quote matters, because the supplier's own
        // product id is published and is a fact about a shelf rather than about anybody's catalogue.
        Assert.DoesNotContain("\"businessId\"", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"productId\"", body, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region Deciding a listing

    /// <summary>
    /// Confirming a listing over the wire: the listing is confirmed with the pack quantity the
    /// reviewer corrected, the fresh reading it was holding becomes its accepted price, and both are
    /// reported. The decision is audited with the actor, and the trail carries no business.
    /// </summary>
    [Fact]
    public async Task Confirming_a_listing_accepts_its_fresh_reading_and_is_audited()
    {
        var arranged = await ArrangeAsync();
        _factory.ClearLog();

        var response = await _factory.AsAdministrator().PostAsJsonAsync(
            $"{ReviewUrl}/listings/{arranged.ListingId}/decision",
            Confirm(unitsPerPack: 36));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var decision = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.True(decision.GetProperty("priceAccepted").GetBoolean());
        Assert.False(decision.GetProperty("priceExceptionUsed").GetBoolean());
        Assert.Equal(42.50m, decision.GetProperty("acceptedPackPrice").GetDecimal());
        Assert.Equal(36, decision.GetProperty("confirmedUnitsPerPack").GetInt32());

        var listing = await ListingAsync(arranged.ListingId);
        Assert.Equal(SupplierListingMatchStatus.Confirmed, listing.MatchStatus);
        Assert.Equal(36, listing.UnitsPerPack);
        Assert.Equal(42.50m, Assert.Single(await ObservationsAsync()).PackPrice);

        var audit = Assert.Single(
            _factory.LoggedEntries(),
            entry => entry.Contains("Price catalogue review audit", StringComparison.Ordinal));
        Assert.Contains("ListingConfirmed", audit, StringComparison.Ordinal);
        Assert.Contains(
            SuperAdminBusinessesApiFactory.AdministratorObjectId,
            audit,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            SuperAdminBusinessesApiFactory.TradingBusinessName,
            audit,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The sixth criterion over the wire: an out-of-band price is refused without the exception and
    /// accepted with it - and the exception is audited <strong>separately</strong> from the listing
    /// decision, which is what the issue asks for.
    /// </summary>
    [Fact]
    public async Task An_approved_price_exception_is_audited_separately()
    {
        var arranged = await ArrangeAsync(packPrice: 9.00m, lastAcceptedPackPrice: 40.00m);
        var client = _factory.AsAdministrator();

        var refused = await client.PostAsJsonAsync(
            $"{ReviewUrl}/listings/{arranged.ListingId}/decision",
            Confirm());
        var refusedBody = JsonDocument.Parse(await refused.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
        Assert.False(refusedBody.GetProperty("priceAccepted").GetBoolean());
        Assert.Equal(
            SupplierListingReviewPolicy.PriceExceptionNotApprovedReason,
            refusedBody.GetProperty("priceReason").GetString());

        // The listing is confirmed now, so the exception is tried on a second flagged listing - which
        // is the state #937 leaves one in when a price moves past the band.
        var flagged = await ArrangeListingAsync(
            arranged.MasterProductId,
            "SKU-FLAGGED",
            SupplierListingMatchStatus.NeedsReview,
            packPrice: 9.00m,
            lastAcceptedPackPrice: 40.00m,
            runId: arranged.RunId,
            workItemId: arranged.WorkItemId);

        _factory.ClearLog();

        var accepted = await client.PostAsJsonAsync(
            $"{ReviewUrl}/listings/{flagged}/decision",
            Confirm() with { ApprovePriceException = true });
        var acceptedBody = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.True(acceptedBody.GetProperty("priceAccepted").GetBoolean());
        Assert.True(acceptedBody.GetProperty("priceExceptionUsed").GetBoolean());

        var audit = _factory.LoggedEntries()
            .Where(entry => entry.Contains("Price catalogue review audit", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(2, audit.Count);
        Assert.Contains(audit, entry => entry.Contains("ListingConfirmed", StringComparison.Ordinal));
        Assert.Contains(
            audit,
            entry => entry.Contains("PriceExceptionApproved", StringComparison.Ordinal));
    }

    /// <summary>A listing nothing holds is <c>404</c>, and one already decided is <c>409</c>.</summary>
    [Fact]
    public async Task An_unknown_listing_is_not_found_and_a_decided_one_is_a_conflict()
    {
        var arranged = await ArrangeAsync();
        var client = _factory.AsAdministrator();

        var unknown = await client.PostAsJsonAsync(
            $"{ReviewUrl}/listings/424242/decision",
            Confirm());
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        Assert.Equal(
            HttpStatusCode.OK,
            (await client.PostAsJsonAsync(
                $"{ReviewUrl}/listings/{arranged.ListingId}/decision",
                Confirm())).StatusCode);

        var again = await client.PostAsJsonAsync(
            $"{ReviewUrl}/listings/{arranged.ListingId}/decision",
            Confirm());
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    /// <summary>
    /// A submitted identity the catalogue would not store is <c>400</c> with the Domain's own
    /// sentence, and nothing is written.
    /// </summary>
    [Fact]
    public async Task A_submitted_identity_the_catalogue_would_not_store_is_refused()
    {
        var arranged = await ArrangeAsync();

        var response = await _factory.AsAdministrator().PostAsJsonAsync(
            $"{ReviewUrl}/listings/{arranged.ListingId}/decision",
            Confirm() with { UnitsPerPack = 0 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            SupplierListingMatchStatus.Proposed,
            (await ListingAsync(arranged.ListingId)).MatchStatus);
    }

    #endregion

    #region Matching and undoing

    /// <summary>
    /// The second criterion over the wire: accepting the suggestion matches <strong>both</strong>
    /// businesses' mappings under the key and moves the key's listing to the master product - and the
    /// response names neither business.
    /// </summary>
    [Fact]
    public async Task Accepting_a_suggestion_matches_both_businesses_and_names_neither()
    {
        var arranged = await ArrangeAsync(listingUnderWaitingKey: true);

        var response = await _factory.AsAdministrator().PostAsJsonAsync(
            $"{ReviewUrl}/matches",
            new
            {
                waitingKey = arranged.WaitingKey,
                decision = (int)ProductMatchReviewDecision.AcceptSuggestion,
                suggestionId = arranged.SuggestionId,
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        var decision = JsonDocument.Parse(body).RootElement;
        Assert.Equal(
            arranged.MasterProductId,
            decision.GetProperty("masterProduct").GetProperty("id").GetInt64());
        Assert.Equal(1, decision.GetProperty("listingsMovedCount").GetInt32());
        Assert.DoesNotContain(SuperAdminBusinessesApiFactory.TradingBusinessName, body, StringComparison.Ordinal);
        Assert.DoesNotContain(SecondBusinessName, body, StringComparison.Ordinal);

        var mappings = await MappingsAsync();
        Assert.Equal(2, mappings.Count);
        Assert.All(mappings, mapping =>
        {
            Assert.Equal(ProductPriceTrackingStatus.Matched, mapping.Status);
            Assert.Equal(arranged.MasterProductId, mapping.MasterProductId);
            Assert.Equal(ProductPriceTrackingMatchSource.SuperAdmin, mapping.MatchSource);
        });

        // Each business sees its own mapping and nothing of the other's, which is #938's read and is
        // unchanged by a shared decision.
        Assert.Equal(2, mappings.Select(mapping => mapping.BusinessId).Distinct().Count());
    }

    /// <summary>
    /// A key nothing is waiting under is <c>404</c>: it has been matched, recorded as not found or
    /// withdrawn since the page was read.
    /// </summary>
    [Fact]
    public async Task Deciding_a_key_nothing_is_waiting_under_is_not_found()
    {
        var arranged = await ArrangeAsync();

        var response = await _factory.AsAdministrator().PostAsJsonAsync(
            $"{ReviewUrl}/matches",
            new
            {
                waitingKey = PriceCatalogueWaitingKey.For("Nothing", "1L"),
                decision = (int)ProductMatchReviewDecision.ChooseMasterProduct,
                masterProductId = arranged.MasterProductId,
            });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// The third criterion over the wire: an automatic match is listed separately, can be confirmed,
    /// and can be undone - with the mappings returned to awaiting a match and the proposed listing
    /// moved back to its key.
    /// </summary>
    [Fact]
    public async Task An_automatic_match_can_be_confirmed_and_undone()
    {
        var arranged = await ArrangeAsync(listingUnderWaitingKey: true);
        await ApplyAutomaticMatchAsync(arranged);

        var client = _factory.AsAdministrator();

        var queue = JsonDocument
            .Parse(await (await client.GetAsync(ReviewUrl)).Content.ReadAsStringAsync())
            .RootElement;
        var automatic = Assert.Single(queue.GetProperty("automaticMatches").EnumerateArray());
        Assert.Equal(arranged.SuggestionId, automatic.GetProperty("suggestionId").GetInt64());
        Assert.Equal("Coca-Cola Classic", automatic.GetProperty("searchName").GetString());

        var undone = await client.PostAsync(
            $"{ReviewUrl}/automatic-matches/{arranged.SuggestionId}/undo",
            content: null);
        Assert.Equal(HttpStatusCode.OK, undone.StatusCode);

        var body = JsonDocument.Parse(await undone.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(1, body.GetProperty("listingsMovedCount").GetInt32());

        Assert.All(await MappingsAsync(), mapping =>
        {
            Assert.Equal(ProductPriceTrackingStatus.Requested, mapping.Status);
            Assert.Null(mapping.MasterProductId);
            Assert.Null(mapping.MatchSource);
        });

        var listing = await ListingAsync(arranged.ListingId);
        Assert.Equal(arranged.WaitingKey, listing.WaitingKey);
        Assert.Null(listing.MasterProductId);

        // And undoing it twice is refused: the suggestion is no longer an automatic match waiting to
        // be checked.
        var again = await client.PostAsync(
            $"{ReviewUrl}/automatic-matches/{arranged.SuggestionId}/undo",
            content: null);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    /// <summary>Confirming an automatic match settles the mappings it applied to.</summary>
    [Fact]
    public async Task Confirming_an_automatic_match_settles_the_mappings()
    {
        var arranged = await ArrangeAsync(listingUnderWaitingKey: true);
        await ApplyAutomaticMatchAsync(arranged);

        var response = await _factory.AsAdministrator().PostAsync(
            $"{ReviewUrl}/automatic-matches/{arranged.SuggestionId}/confirm",
            content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.All(await MappingsAsync(), mapping =>
        {
            Assert.Equal(ProductPriceTrackingStatus.Matched, mapping.Status);
            Assert.Equal(arranged.MasterProductId, mapping.MasterProductId);
            Assert.Equal(
                ProductPriceTrackingMatchSource.CollectorAutomaticMatch,
                mapping.MatchSource);
        });
    }

    #endregion

    private const string SecondBusinessName = "Harbour Vending";

    private sealed record Arranged(
        int SupplierPresetId,
        long MasterProductId,
        long SuggestionId,
        long ListingId,
        long RunId,
        long WorkItemId,
        string WaitingKey);

    /// <summary>
    /// Two businesses tracking one product by two spellings of one description, a master product the
    /// matcher proposed for it, a supplier listing and the reading that listing is holding.
    /// </summary>
    private async Task<Arranged> ArrangeAsync(
        decimal packPrice = 42.50m,
        decimal? lastAcceptedPackPrice = null,
        bool listingUnderWaitingKey = false)
    {
        var waitingKey = PriceCatalogueWaitingKey.For("Coca-Cola Classic", "375mL");
        var secondBusinessId = await _factory.SeedBusinessAsync(SecondBusinessName, BusinessStatus.Active);

        int presetId;
        long masterProductId;
        long suggestionId;
        long runId;
        long workItemId;

        await using (var db = _factory.Database())
        {
            // A supplier master record of this test's own, so no shipped preset's cadence or
            // connector decides anything here.
            var preset = new SupplierPreset
            {
                Name = "Alpha Grocer",
                NormalisedName = SupplierPresetPolicy.NormalisedName("Alpha Grocer"),
                IsEnabled = true,
                ServesNationwide = true,
                PriceCheckFreshnessHours = SupplierPriceFreshnessPolicy.DailyCheckLimitHours,
                CreatedAtUtc = Observed,
                UpdatedAtUtc = Observed,
            };
            db.SupplierPresets.Add(preset);

            var master = new MasterProduct
            {
                Brand = "Coca-Cola",
                Name = "Classic",
                SizeValue = 375m,
                SizeUnit = MasterProductSizeUnit.Millilitre,
                NormalisedKey = MasterProductPolicy.NormalisedKey(
                    "Coca-Cola",
                    "Classic",
                    375m,
                    MasterProductSizeUnit.Millilitre),
                CreatedAtUtc = Observed,
                UpdatedAtUtc = Observed,
            };
            db.MasterProducts.Add(master);
            await db.SaveChangesAsync();

            presetId = preset.Id;
            masterProductId = master.Id;

            var suggestion = new ProductMatchSuggestion
            {
                WaitingKey = waitingKey,
                MasterProductId = master.Id,
                Confidence = 0.6m,
                Reason = "The brand and the size agree.",
                CollectorRun = "price-collector-run-3",
                Status = ProductMatchSuggestionStatus.Tentative,
                CreatedAtUtc = Observed,
                UpdatedAtUtc = Observed,
            };
            db.ProductMatchSuggestions.Add(suggestion);

            var run = new PriceCollectorRun
            {
                StartedAtUtc = Observed,
                Status = PriceCollectorRunStatus.Running,
            };
            db.PriceCollectorRuns.Add(run);
            await db.SaveChangesAsync();

            suggestionId = suggestion.Id;
            runId = run.Id;

            var workItem = new PriceCollectorWorkItem
            {
                PriceCollectorRunId = run.Id,
                SupplierPresetId = preset.Id,
                WaitingKey = waitingKey,
                IssuedAtUtc = Observed,
            };
            db.PriceCollectorWorkItems.Add(workItem);

            // Each business's own product, spelled differently on purpose: the two fold to one
            // waiting key, which is what makes them one search and one decision.
            var first = Product(_factory.TradingBusinessId, "Coca-Cola Classic", "375mL");
            var second = Product(secondBusinessId, "coca-cola  classic", "375 ML");
            db.Products.AddRange(first, second);

            await db.SaveChangesAsync();
            workItemId = workItem.Id;

            db.ProductPriceTrackings.AddRange(
                Tracking(first, waitingKey),
                Tracking(second, waitingKey));

            await db.SaveChangesAsync();
        }

        var listingId = await ArrangeListingAsync(
            masterProductId,
            "SKU-COLA-30",
            SupplierListingMatchStatus.Proposed,
            packPrice,
            lastAcceptedPackPrice,
            runId,
            workItemId,
            listingUnderWaitingKey ? waitingKey : null,
            presetId);

        return new Arranged(presetId, masterProductId, suggestionId, listingId, runId, workItemId, waitingKey);
    }

    /// <summary>
    /// One supplier listing in the match status a test is about, holding one reading and optionally
    /// with an accepted price behind it so the band has something to be a band around.
    /// </summary>
    private async Task<long> ArrangeListingAsync(
        long masterProductId,
        string supplierProductId,
        SupplierListingMatchStatus status,
        decimal packPrice,
        decimal? lastAcceptedPackPrice,
        long runId,
        long workItemId,
        string? waitingKey = null,
        int? supplierPresetId = null)
    {
        await using var db = _factory.Database();

        var presetId = supplierPresetId
            ?? (await db.SupplierPresets.AsNoTracking().FirstAsync(preset => preset.Name == "Alpha Grocer")).Id;

        var listing = new SupplierListing
        {
            SupplierPresetId = presetId,
            MasterProductId = waitingKey is null ? masterProductId : null,
            WaitingKey = waitingKey,
            OriginWaitingKey = waitingKey,
            SupplierProductId = supplierProductId,
            Url = "https://alpha.example/cola",
            Title = "Alpha Grocer Coca-Cola Classic 375mL 30 pack",
            Variant = "Classic",
            SizeValue = 375m,
            SizeUnit = MasterProductSizeUnit.Millilitre,
            UnitsPerPack = 30,
            Barcode = "0012345678905",
            BarcodeScope = BarcodeScope.Unit,
            MatchStatus = status,
            CreatedAtUtc = Observed,
            UpdatedAtUtc = Observed,
        };

        db.SupplierListings.Add(listing);
        await db.SaveChangesAsync();

        if (lastAcceptedPackPrice is { } last)
        {
            var observation = new SupplierPriceObservation
            {
                SupplierListingId = listing.Id,
                PriceCollectorRunId = runId,
                PackPrice = last,
                Currency = SupplierPriceObservationPolicy.Currency,
                PriceBasis = SupplierPriceBasis.InclusiveOfGst,
                StockStatus = SupplierStockStatus.InStock,
                ObservedAtUtc = Observed.AddDays(-1),
                CreatedAtUtc = Observed.AddDays(-1),
            };

            db.SupplierPriceObservations.Add(observation);
            await db.SaveChangesAsync();

            listing.LatestObservationId = observation.Id;
            listing.LastCheckedAtUtc = observation.ObservedAtUtc;
            listing.LastPriceChangeAtUtc = observation.ObservedAtUtc;
        }

        db.SupplierListingPendingObservations.Add(new SupplierListingPendingObservation
        {
            SupplierListingId = listing.Id,
            PriceCollectorRunId = runId,
            PriceCollectorWorkItemId = workItemId,
            Reason = PriceCollectorResultPolicy.ListingNotConfirmedReason,
            PackPrice = packPrice,
            Currency = SupplierPriceObservationPolicy.Currency,
            PriceBasis = SupplierPriceBasis.InclusiveOfGst,
            SupplierGstRate = 0.1000m,
            StockStatus = SupplierStockStatus.InStock,
            ObservedAtUtc = Observed,
            Variant = "Classic",
            SizeValue = 375m,
            SizeUnit = MasterProductSizeUnit.Millilitre,
            UnitsPerPack = 30,
            Barcode = "0012345678905",
            BarcodeScope = BarcodeScope.Unit,
            Title = "Alpha Grocer Coca-Cola Classic 375mL 30 pack",
            RecordedAtUtc = Observed,
        });

        await db.SaveChangesAsync();

        return listing.Id;
    }

    /// <summary>
    /// The state #941 leaves an automatic match in: the mappings auto-matched, the suggestion applied,
    /// and the key's listings moved to the master product keeping their own status and origin key.
    /// </summary>
    private async Task ApplyAutomaticMatchAsync(Arranged arranged)
    {
        await using var db = _factory.Database();

        foreach (var mapping in await db.ProductPriceTrackings
            .Where(mapping => mapping.WaitingKey == arranged.WaitingKey)
            .ToListAsync())
        {
            mapping.Status = ProductPriceTrackingStatus.AutoMatched;
            mapping.MasterProductId = arranged.MasterProductId;
            mapping.MatchSource = ProductPriceTrackingMatchSource.CollectorAutomaticMatch;
        }

        foreach (var listing in await db.SupplierListings
            .Where(listing => listing.WaitingKey == arranged.WaitingKey)
            .ToListAsync())
        {
            listing.MasterProductId = arranged.MasterProductId;
            listing.WaitingKey = null;
        }

        var suggestion = await db.ProductMatchSuggestions
            .SingleAsync(candidate => candidate.Id == arranged.SuggestionId);
        suggestion.Status = ProductMatchSuggestionStatus.AutoApplied;
        suggestion.Confidence = 1m;

        await db.SaveChangesAsync();
    }

    private static Product Product(int businessId, string name, string unit) =>
        new()
        {
            BusinessId = businessId,
            Name = name,
            Unit = unit,
            CreatedAt = Observed,
            UpdatedAt = Observed,
        };

    /// <summary>
    /// One business's statement that it wants that product priced, with the search text captured as
    /// the switch of #938 captures it - so the two businesses' two spellings fold to one key.
    /// </summary>
    private static ProductPriceTracking Tracking(Product product, string waitingKey) =>
        new()
        {
            BusinessId = product.BusinessId,
            ProductId = product.Id,
            Status = ProductPriceTrackingStatus.Requested,
            WaitingKey = waitingKey,
            SearchName = product.Name.Trim(),
            UnitLabel = (product.Unit ?? string.Empty).Trim(),
            CreatedAtUtc = Observed,
            UpdatedAtUtc = Observed,
        };

    /// <summary>
    /// The body a confirmation submits, as the wire carries it: the declared enum values as numbers,
    /// which is this API's contract for every enum it publishes.
    /// </summary>
    private sealed record ConfirmBody(
        int Outcome,
        int? UnitsPerPack,
        string Barcode,
        int BarcodeScope,
        string Variant,
        decimal SizeValue,
        int SizeUnit,
        bool ApprovePriceException);

    private static ConfirmBody Confirm(int? unitsPerPack = 30) =>
        new(
            (int)SupplierListingReviewOutcome.Confirmed,
            unitsPerPack,
            "0012345678905",
            (int)BarcodeScope.Unit,
            "Classic",
            375m,
            (int)MasterProductSizeUnit.Millilitre,
            ApprovePriceException: false);

    private async Task<SupplierListing> ListingAsync(long listingId)
    {
        await using var db = _factory.Database();
        return await db.SupplierListings.AsNoTracking().SingleAsync(listing => listing.Id == listingId);
    }

    private async Task<IReadOnlyList<SupplierPriceObservation>> ObservationsAsync()
    {
        await using var db = _factory.Database();
        return await db.SupplierPriceObservations.AsNoTracking().ToListAsync();
    }

    private async Task<IReadOnlyList<ProductPriceTracking>> MappingsAsync()
    {
        await using var db = _factory.Database();
        return await db.ProductPriceTrackings.AsNoTracking().OrderBy(mapping => mapping.Id).ToListAsync();
    }

    private async Task<IReadOnlyList<HttpResponseMessage>> AllEndpointsAsync(HttpClient client)
    {
        var arranged = await ArrangeAsync();

        return
        [
            await client.GetAsync(ReviewUrl),
            .. await AllWritesAsync(client, arranged),
        ];
    }

    private static async Task<IReadOnlyList<HttpResponseMessage>> AllWritesAsync(
        HttpClient client,
        Arranged arranged) =>
        [
            await client.PostAsJsonAsync(
                $"{ReviewUrl}/listings/{arranged.ListingId}/decision",
                Confirm()),
            await client.PostAsJsonAsync(
                $"{ReviewUrl}/matches",
                new
                {
                    waitingKey = arranged.WaitingKey,
                    decision = (int)ProductMatchReviewDecision.AcceptSuggestion,
                    suggestionId = arranged.SuggestionId,
                }),
            await client.PostAsync(
                $"{ReviewUrl}/automatic-matches/{arranged.SuggestionId}/confirm",
                content: null),
            await client.PostAsync(
                $"{ReviewUrl}/automatic-matches/{arranged.SuggestionId}/undo",
                content: null),
        ];

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();

        return JsonDocument.Parse(body).RootElement.TryGetProperty("code", out var code)
            ? code.GetString()
            : null;
    }
}
