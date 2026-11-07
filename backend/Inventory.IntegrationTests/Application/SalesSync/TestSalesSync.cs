using Inventory.Application.Nayax;
using Inventory.Application.SalesSync;
using InventoryApi.Tests.Application.Gst;
using InventoryApi.Tests.Application.Nayax;
using InventoryApi.Tests.Application.Providers;
using InventoryApi.Tests.Application.Time;
using Moq;

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
        FakeNayaxOperationCredentials? credentials = null,
        NayaxSalesSyncLock? syncLock = null)
    {
        var sharedClock = clock ?? new FakeClock(FixedNow);
        return new SyncLatestNayaxSales(
            nayax,
            store,
            new NayaxSalesSyncRateLimiter(sharedClock),
            freshness ?? new NayaxSalesSyncFreshness(sharedClock),
            // Real, and - unless the caller supplies one - private to each use case this builds, so a
            // test that is not about the lock never waits for another test's run. The tests that are
            // about it share one on purpose (issue #1104).
            syncLock ?? new NayaxSalesSyncLock(),
            credentials ?? new FakeNayaxOperationCredentials(),
            new FakeCurrentBusinessProvider(BusinessId),
            coverage ?? new FakeProviderDataCoverageStore(),
            sharedClock);
    }

    /// <summary>
    /// A store double that persists nothing and has nothing pending, for a test that is about the use
    /// case rather than about persistence (issue #1103).
    ///
    /// <para>Both methods are stubbed on purpose. <see cref="SyncLatestNayaxSales"/> drains the
    /// pending rebuilds after <em>every</em> persist and reads the result, so a store that answered
    /// nothing at all would fail the test for a reason that has nothing to do with what it asserts.
    /// A test about the drain overrides the setup it is interested in.</para>
    /// </summary>
    internal static Mock<ILatestNayaxSalesStore> StoreMock()
    {
        var store = new Mock<ILatestNayaxSalesStore>();

        store.Setup(x => x.PersistLatestSalesAsync(
                It.IsAny<IReadOnlyList<NayaxLastSalesReport>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LatestNayaxSalesPersistResult(
                new Dictionary<long, DateTime> { [10] = new(2026, 9, 2, 14, 30, 0, DateTimeKind.Utc) },
                InsertedCount: 1));

        store.Setup(x => x.RebuildInventoryCostsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InventoryCostRebuildDrainResult([], []));

        return store;
    }

    /// <summary>
    /// A store double that persists nothing, affects nothing and has nothing pending - the shape a
    /// test which must prove the store was never reached starts from.
    /// </summary>
    internal static Mock<ILatestNayaxSalesStore> EmptyStoreMock()
    {
        var store = StoreMock();

        store.Setup(x => x.PersistLatestSalesAsync(
                It.IsAny<IReadOnlyList<NayaxLastSalesReport>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LatestNayaxSalesPersistResult(
                new Dictionary<long, DateTime>(), InsertedCount: 0));

        return store;
    }
}
