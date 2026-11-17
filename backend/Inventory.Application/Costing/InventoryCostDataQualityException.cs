namespace Inventory.Application.Costing;

/// <summary>
/// Thrown by a non-dry-run <see cref="IRebuildProductCost.RebuildAsync"/> when the replayed cost
/// history has a fatal data-quality issue (issue #296; formerly
/// <c>InventoryApi.Services.InventoryCostDataQualityException</c>).
///
/// <para><strong>Its message is written for the operator and is published verbatim</strong> (issue
/// #1240). Every fatal issue the replay reports names the product it is about through
/// <c>Inventory.Domain.Products.ProductLabel</c> and the movement or sale that carries it, and
/// nothing else: no path, no connection detail and no other business's data. The refusal depends on
/// the product's current history, so <c>InventoryApi.Http.DomainExceptionHandler</c> maps this type
/// - by name, never through the <see cref="InvalidOperationException"/> it derives from - to a
/// <c>409 Conflict</c> whose detail is that message, and logs it once at <c>Warning</c>. Before
/// that it was unmapped, which answered a manual adjustment, a pick-list refill and a Take
/// Inventory apply with a generic <c>500</c> that named no product.</para>
///
/// <para>A caller that catches it deliberately still decides its own answer: the purchases
/// controller's <see cref="InvalidOperationException"/> catch turns it into the <c>400</c> it has
/// always returned, because a controller catch runs before any exception handler.</para>
/// </summary>
public sealed class InventoryCostDataQualityException : InvalidOperationException
{
    public InventoryCostDataQualityException(string message) : base(message) { }
}
