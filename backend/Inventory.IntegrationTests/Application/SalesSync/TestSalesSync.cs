using Inventory.Application.Nayax;
using Inventory.Application.SalesSync;
using InventoryApi.Tests.Application.Gst;
using InventoryApi.Tests.Application.Nayax;
using InventoryApi.Tests.Application.Providers;
using InventoryApi.Tests.Application.Time;

namespace InventoryApi.Tests.Application.SalesSync;

/// <summary>
/// Builds <see cref="SyncLatestNayaxSales"/> for a test that is about the synchronization itself
/// rather than about its per-business run limit (issue #681) or its recent-sync skip (issue #843).
///
/// The limiter is real, not a stub, with a clock fixed at one instant: a test that runs the
/// synchronization once or twice therefore spends real allowances and never reaches the limit, so
/// the rule stays exercised rather than switched off. The tests that are about the limit construct
/// their own limiter and clock and move them deliberately.
///
/// The freshness memory is real too, and - unless the caller supplies one - private to each use case
/// this builds, so one test's successful run cannot make another test's skip. A test that runs the
/// same use-case instance twice is asking for two real runs and passes <c>force: true</c> for the
/// second; the tests that are about the skip share a freshness memory on purpose.
///
/// The credentials are a usable connection at revision 1, because that is the ordinary case every
/// test here is about: the refusals the use case resolves before deciding anything are covered by
/// <c>SyncLatestNayaxSalesFreshnessTests</c>.
/// </summary>
internal static class TestSalesSync
{
    private static readonly DateTime FixedNow = new(2026, 10, 15, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>The business every test here acts as, which is also the limiter's bucket.</summary>
    internal const int BusinessId = 1;

    internal static SyncLatestNayaxSales UseCase(
        INayaxLynxClient nayax,
        ILatestNayaxSalesStore store,
        FakeClock? clock = null,
        FakeProviderDataCoverageStore? coverage = null,
        NayaxSalesSyncFreshness? freshness = null,
        FakeNayaxOperationCredentials? credentials = null)
    {
        var sharedClock = clock ?? new FakeClock(FixedNow);
        return new SyncLatestNayaxSales(
            nayax,
            store,
            new NayaxSalesSyncRateLimiter(sharedClock),
            freshness ?? new NayaxSalesSyncFreshness(sharedClock),
            credentials ?? new FakeNayaxOperationCredentials(),
            new FakeCurrentBusinessProvider(BusinessId),
            coverage ?? new FakeProviderDataCoverageStore(),
            sharedClock);
    }
}
