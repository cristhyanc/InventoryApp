using Inventory.Application.Nayax;
using Inventory.Application.SalesSync;
using InventoryApi.Tests.Application.Gst;
using InventoryApi.Tests.Application.Time;

namespace InventoryApi.Tests.Application.SalesSync;

/// <summary>
/// Builds <see cref="SyncLatestNayaxSales"/> for a test that is about the synchronization itself
/// rather than about its per-business run limit (issue #681).
///
/// The limiter is real, not a stub, with a clock fixed at one instant: a test that runs the
/// synchronization once or twice therefore spends real allowances and never reaches the limit, so
/// the rule stays exercised rather than switched off. The tests that are about the limit construct
/// their own limiter and clock and move them deliberately.
/// </summary>
internal static class TestSalesSync
{
    private static readonly DateTime FixedNow = new(2026, 10, 15, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>The business every test here acts as, which is also the limiter's bucket.</summary>
    internal const int BusinessId = 1;

    internal static SyncLatestNayaxSales UseCase(
        INayaxLynxClient nayax,
        ILatestNayaxSalesStore store,
        FakeClock? clock = null) =>
        new(
            nayax,
            store,
            new NayaxSalesSyncRateLimiter(clock ?? new FakeClock(FixedNow)),
            new FakeCurrentBusinessProvider(BusinessId));
}
