using System.Net;
using System.Text.Json;
using Inventory.Domain.ReceiptScanning;
using Inventory.Domain.Subscriptions;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Models;
using InventoryApi.Auth;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// The Super Admin's view of one business's monthly receipt-scan usage over real HTTP (issue #1164,
/// sub-issue C of #975): who may reach it, what it reports, and that it changes nothing.
///
/// <para>Everything here goes through the complete pipeline - authentication, <c>[Authorize]</c>, the
/// platform-admin policy, <c>[RequiredScope]</c>, <c>RolePreviewMiddleware</c>,
/// <c>BusinessScopeMiddleware</c>'s independent re-check of the same policy, the tenant query
/// filters, <c>BusinessOwnershipEnforcer</c> and relational SQLite with the real migration history -
/// so the authorization and the tenancy boundary under test are the real ones. Only the
/// authentication is synthetic.</para>
///
/// <para><strong>Two active businesses throughout</strong>, because the claim that matters most here
/// is that a count belongs to exactly one of them.</para>
/// </summary>
public class SuperAdminReceiptScanUsageApiTests
    : IClassFixture<SuperAdminReceiptScanUsageApiTests.ApiFactory>, IAsyncLifetime
{
    private readonly ApiFactory _factory;

    public SuperAdminReceiptScanUsageApiTests(ApiFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// A business role reaches nothing here, Owner included: a business role is not platform
    /// authority, and this controller declares no capability any role could satisfy. What a business
    /// has spent at the reader is the platform's own question.
    /// </summary>
    [Theory]
    [InlineData(SuperAdminBusinessesApiFactory.TradingOwnerObjectId)]
    [InlineData(SuperAdminBusinessesApiFactory.TradingManagerObjectId)]
    [InlineData(SuperAdminBusinessesApiFactory.StrangerObjectId)]
    public async Task A_caller_who_is_not_the_configured_administrator_is_refused(string objectId)
    {
        var response = await _factory.Authenticated(objectId).GetAsync(Route(_factory.TradingBusinessId));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_unauthenticated_request_is_unauthorized()
    {
        var response = await _factory.CreateClient().GetAsync(Route(_factory.TradingBusinessId));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// The delegated scope is required of the administrator too: platform authority is an additional
    /// requirement on a delegated API call, never a replacement for one.
    /// </summary>
    [Fact]
    public async Task The_administrator_still_needs_the_delegated_scope()
    {
        var response = await _factory
            .WithoutScope(SuperAdminBusinessesApiFactory.AdministratorObjectId)
            .GetAsync(Route(_factory.TradingBusinessId));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// A role preview may make this read, by the same central rule that lets it make every other
    /// platform-admin <c>GET</c>: <c>RolePreviewMiddleware</c> refuses a request that could change
    /// something, and this one cannot. Previewing a business role is not a way to <em>gain</em>
    /// platform authority either - the administrator is still themselves, which is why the policy
    /// succeeds, and an ordinary member setting the same header is refused exactly as above.
    /// </summary>
    [Fact]
    public async Task A_role_preview_may_make_this_read_because_it_changes_nothing()
    {
        await _factory.SeedScanUsageAsync(_factory.TradingBusinessId, 8);

        var response = await _factory.Previewing(nameof(BusinessRole.Owner))
            .GetAsync(Route(_factory.TradingBusinessId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            8,
            JsonDocument.Parse(await response.Content.ReadAsStringAsync())
                .RootElement.GetProperty("used").GetInt32());
    }

    /// <summary>
    /// <strong>The issue's last acceptance criterion.</strong> The administrator reads how many scans
    /// one business has used in its own current month, with the cap and what is left - and the month
    /// is named, because a count shown without its month is a count against an unknown cap.
    /// </summary>
    [Fact]
    public async Task The_administrator_reads_one_businesss_scans_used_this_month()
    {
        await _factory.SeedScanUsageAsync(_factory.TradingBusinessId, 37);

        var usage = await ReadAsync(_factory.TradingBusinessId);

        Assert.Equal(_factory.TradingBusinessId, usage.GetProperty("businessId").GetInt32());
        Assert.Equal(_factory.CurrentMonthLabel(), usage.GetProperty("month").GetString());
        Assert.Equal(37, usage.GetProperty("used").GetInt32());
        Assert.Equal(PlanCatalogue.MonthlyReceiptScanLimit - 37, usage.GetProperty("remaining").GetInt32());
        Assert.Equal(PlanCatalogue.MonthlyReceiptScanLimit, usage.GetProperty("limit").GetInt32());
    }

    /// <summary>
    /// A business that has never scanned has used none of its allowance. Zero and the whole cap
    /// remaining, which is true - not a <c>404</c>, which would say the business does not exist.
    /// </summary>
    [Fact]
    public async Task A_business_that_has_never_scanned_reads_zero()
    {
        var usage = await ReadAsync(_factory.TradingBusinessId);

        Assert.Equal(0, usage.GetProperty("used").GetInt32());
        Assert.Equal(PlanCatalogue.MonthlyReceiptScanLimit, usage.GetProperty("remaining").GetInt32());
    }

    /// <summary>
    /// A business at its cap reads nothing remaining, which is the state the platform page exists to
    /// make visible.
    /// </summary>
    [Fact]
    public async Task A_business_at_its_cap_reads_nothing_remaining()
    {
        await _factory.SeedScanUsageAsync(
            _factory.TradingBusinessId, PlanCatalogue.MonthlyReceiptScanLimit);

        var usage = await ReadAsync(_factory.TradingBusinessId);

        Assert.Equal(PlanCatalogue.MonthlyReceiptScanLimit, usage.GetProperty("used").GetInt32());
        Assert.Equal(0, usage.GetProperty("remaining").GetInt32());
    }

    /// <summary>
    /// Each business is answered for itself, and the route value is the whole of what selects which:
    /// one business's count never appears in the other's answer.
    /// </summary>
    [Fact]
    public async Task Each_business_is_answered_for_its_own_count()
    {
        await _factory.SeedScanUsageAsync(_factory.TradingBusinessId, 5);
        await _factory.SeedScanUsageAsync(_factory.OtherBusinessId, 61);

        Assert.Equal(5, (await ReadAsync(_factory.TradingBusinessId)).GetProperty("used").GetInt32());
        Assert.Equal(61, (await ReadAsync(_factory.OtherBusinessId)).GetProperty("used").GetInt32());
    }

    /// <summary>
    /// An id that matches no business row is <c>404</c>: the route value is the subject of the read
    /// and is checked against the stored business, so a number nobody owns is the absence of a
    /// resource rather than an empty count.
    /// </summary>
    [Fact]
    public async Task An_unknown_business_is_not_found()
    {
        var response = await _factory
            .AsAdministrator()
            .GetAsync(Route(_factory.TradingBusinessId + _factory.OtherBusinessId + 9_999));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// A deactivated business is still readable: what it spent is a fact about the past, and an
    /// administrator reviewing costs needs it. The read changes nothing either way.
    /// </summary>
    [Fact]
    public async Task A_deactivated_business_is_still_readable()
    {
        await _factory.SeedScanUsageAsync(_factory.TradingBusinessId, 3);
        await _factory.SetBusinessStatusAsync(_factory.TradingBusinessId, BusinessStatus.Deactivated);

        Assert.Equal(3, (await ReadAsync(_factory.TradingBusinessId)).GetProperty("used").GetInt32());
    }

    /// <summary>
    /// <strong>Reading changes nothing.</strong> There is one route and it is a <c>GET</c>: every
    /// other method on it is refused by routing, and the stored counters are byte-for-byte what they
    /// were. Buying extra scans and resetting a counter are explicitly out of scope on issue #1164,
    /// and a counter somebody could edit is not evidence of what was spent.
    /// </summary>
    [Fact]
    public async Task The_surface_is_read_only_and_leaves_every_counter_as_it_was()
    {
        await _factory.SeedScanUsageAsync(_factory.TradingBusinessId, 12);
        var before = await _factory.StoredCountersAsync();

        var client = _factory.AsAdministrator();
        var route = Route(_factory.TradingBusinessId);

        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete, HttpMethod.Patch })
        {
            var response = await client.SendAsync(new HttpRequestMessage(method, route));
            // Whichever way it is refused - no endpoint matches the method, or the fallback refuses
            // it - what matters is that it is refused and that nothing is written.
            Assert.True(
                response.StatusCode
                    is HttpStatusCode.NotFound
                    or HttpStatusCode.MethodNotAllowed
                    or HttpStatusCode.Forbidden,
                $"{method} on the usage route answered {response.StatusCode}.");
        }

        await ReadAsync(_factory.TradingBusinessId);

        Assert.Equal(before, await _factory.StoredCountersAsync());
    }

    private static string Route(int businessId) =>
        $"/api/super-admin/businesses/{businessId}/receipt-scan-usage";

    private async Task<JsonElement> ReadAsync(int businessId)
    {
        var response = await _factory.AsAdministrator().GetAsync(Route(businessId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    /// <summary>
    /// The Super Admin fixture's world with a second active business, so no answer about a count can
    /// be the only one possible.
    /// </summary>
    public sealed class ApiFactory : SuperAdminBusinessesApiFactory
    {
        public const string OtherBusinessOwnerObjectId = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";

        /// <summary>The second business, which is never the one a single-business assertion is about.</summary>
        public int OtherBusinessId { get; private set; }

        public HttpClient Previewing(string role)
        {
            var client = Authenticated(AdministratorObjectId);
            client.DefaultRequestHeaders.TryAddWithoutValidation(RolePreviewHeader.Name, role);
            return client;
        }

        /// <summary>
        /// The business month this host is in right now, as the response publishes it. Read through
        /// the same policy the server uses rather than written out, so a run on the first or last day
        /// of a month cannot make this test disagree with the API about which month it is.
        /// </summary>
        public string CurrentMonthLabel() =>
            ReceiptScanQuotaPolicy.MonthLabel(ReceiptScanQuotaPolicy.MonthKeyFor(SydneyToday()));

        /// <summary>Puts one business's count for its current month at <paramref name="scanCount"/>.</summary>
        public async Task SeedScanUsageAsync(int businessId, int scanCount)
        {
            await using var db = Database();
            db.ReceiptScanUsages.Add(new ReceiptScanUsage
            {
                BusinessId = businessId,
                MonthKey = ReceiptScanQuotaPolicy.MonthKeyFor(SydneyToday()),
                ScanCount = scanCount,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        /// <summary>
        /// Every stored counter as text, so "nothing changed" is a comparison rather than an
        /// inference.
        /// </summary>
        public async Task<string> StoredCountersAsync()
        {
            await using var db = Database();

            return string.Join(
                '|',
                await db.ReceiptScanUsages
                    .AsNoTracking()
                    .OrderBy(row => row.Id)
                    .Select(row => $"{row.BusinessId}:{row.MonthKey}:{row.ScanCount}")
                    .ToListAsync());
        }

        public new async Task ResetAsync()
        {
            await ClearUsageAsync();
            await base.ResetAsync();

            OtherBusinessId = await SeedBusinessAsync("Second Vending Co", BusinessStatus.Active);
            await SeedMembershipAsync(
                OtherBusinessId, OtherBusinessOwnerObjectId, BusinessRole.Owner, isActive: true);
        }

        private async Task ClearUsageAsync()
        {
            await using var db = Database();
            db.ReceiptScanUsages.RemoveRange(await db.ReceiptScanUsages.ToListAsync());
            await db.SaveChangesAsync();
        }

        /// <summary>Today in the zone every business this fixture seeds is configured in.</summary>
        private static DateTime SydneyToday() =>
            TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.UtcNow,
                TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney")).Date;
    }
}
