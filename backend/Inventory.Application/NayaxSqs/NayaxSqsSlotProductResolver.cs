using Inventory.Application.Nayax;

namespace Inventory.Application.NayaxSqs;

/// <summary>
/// Resolves the machine slot a Nayax SQS message reports (<c>Product Code in Map</c>, the MDB code)
/// to Nayax's own product id (issue #1102).
///
/// <para>It is the <strong>fallback</strong> for a message that states no usable product id of its
/// own. The nested product entry carries <c>Product ID</c>, Nayax's own identifier, which the parser
/// maps straight onto the sale; but it is a selectable column and the value is not always a usable
/// identifier, and the slot is then the only thing the message says about which product was vended
/// besides its name. The <c>lastSales</c> payload always carries the id directly, so this step has
/// no counterpart on that path.</para>
/// </summary>
public interface INayaxSqsSlotProductResolver
{
    /// <summary>
    /// The Nayax product id the machine's product map records against
    /// <paramref name="productSlot"/>, or <see langword="null"/> when there is no single answer -
    /// a missing slot, a machine with no product map, a slot the map does not hold, a map entry
    /// carrying no product id, or two entries claiming the same slot. The caller then leaves the
    /// sale's <c>NayaxProductId</c> unset and the existing unambiguous-name fallback in
    /// <c>ProductMatcher.MatchLinked</c> applies, exactly as it does today for a
    /// <c>lastSales</c> item whose product id this business has recorded on no product.
    /// </summary>
    Task<long?> ResolveAsync(long machineId, int? productSlot, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class NayaxSqsSlotProductResolver : INayaxSqsSlotProductResolver
{
    private readonly INayaxLynxClient _nayax;

    public NayaxSqsSlotProductResolver(INayaxLynxClient nayax) => _nayax = nayax;

    /// <inheritdoc />
    public async Task<long?> ResolveAsync(
        long machineId, int? productSlot, CancellationToken cancellationToken = default)
    {
        // Nothing to look up, and nothing to read Nayax for: a message with no readable slot
        // resolves to no product id whatever the machine's map says.
        if (productSlot is not { } slot)
            return null;

        // The cached selections read (issue #842), deliberately not the fresh one. A product map
        // changes when an operator reconfigures the machine, not between two transactions of the
        // same minute, so a queue draining a hundred messages for one machine makes one Nayax call
        // rather than a hundred. Nothing here depends on the current moment the way a refill plan,
        // a recorded count or a costing baseline does.
        var machineProducts = await _nayax.GetMachineProductsAsync(machineId, cancellationToken);

        var resolved = machineProducts
            .Where(product => product.MDBCode == slot && product.NayaxProductID.HasValue)
            .Select(product => product.NayaxProductID!.Value)
            .Distinct()
            .Take(2)
            .ToList();

        // Two different products on one slot is a product map this code cannot read an answer out
        // of, so it answers none - the same fail-closed shape as the Domain's unambiguous-name
        // rule, rather than picking whichever row came back first.
        return resolved.Count == 1 ? resolved[0] : null;
    }
}
