using Inventory.Application.Time;
using InventoryApi.Tests.Application.Time;

namespace InventoryApi.Tests;

/// <summary>
/// The application clock an adapter test hands a store that needs one, when the test is not about
/// time at all.
///
/// <para>It exists because the purchase and operating-expense adapters take <see cref="IClock"/>
/// since issue #968 - an attachment storage reservation is judged expired against it - and the
/// dozens of tests that only record a purchase or an expense should not each have to invent an
/// instant. A test that <em>is</em> about expiry builds its own clock and moves it.</para>
/// </summary>
internal static class TestClocks
{
    /// <summary>A fixed instant, the same one <see cref="TestPurchaseUseCases"/> records with.</summary>
    public static IClock Fixed { get; } = new FakeClock(new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc));
}
