using Inventory.Application.Documents;
using Inventory.Application.Purchases;
using Inventory.Application.Tenancy;
using Inventory.Application.Time;
using Inventory.Domain.Tenancy;
using InventoryApi.Tests.Application.Tenancy;
using InventoryApi.Tests.Application.Time;

namespace InventoryApi.Tests;

/// <summary>
/// Builds the purchase use cases for tests, following <see cref="TestCostingUseCases"/>.
///
/// <para>It exists because <see cref="UploadPurchase"/> gained the actor port in issue #974: a
/// supplier-order completion must name the operator who decided it, so the use case resolves the
/// actor the same way <c>ApplyInventoryCostRepair</c> does. Every test that only records a purchase
/// gets the identified operator below and is otherwise unaffected.</para>
/// </summary>
internal static class TestPurchaseUseCases
{
    /// <summary>The identified operator a purchase test records as, unless it supplies its own accessor.</summary>
    public static readonly ActorIdentity Operator = Actor("44444444-4444-4444-4444-444444444444");

    public static UploadPurchase Upload(
        IPurchaseStore store,
        IDocumentStorage documents,
        IClock? clock = null,
        IAuthenticatedActorAccessor? actors = null) =>
        new(
            store,
            documents,
            actors ?? FakeAuthenticatedActorAccessor.Identified(Operator),
            clock ?? new FakeClock(new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc)));

    private static ActorIdentity Actor(string objectId)
    {
        ActorIdentity.TryCreate("22222222-2222-2222-2222-222222222222", objectId, out var actor);
        return actor!;
    }
}
