using Inventory.Application.MachineStock;
using Inventory.Application.Nayax;
using Inventory.Application.Time;
using Inventory.Domain.MachineStock;

namespace Inventory.Infrastructure.Nayax;

/// <summary>
/// The machine-stock observation adapter (issue #56): what the machine itself reports it is holding,
/// read from the live Nayax machine-product slots through the existing
/// <see cref="INayaxLynxClient.GetMachineProductsFreshAsync"/> port.
///
/// It is a translation and nothing else. It carries <c>PAR</c> and <c>MissingStockByMDB</c> across
/// exactly as Nayax reports them - including their absence, which the published contract allows for
/// both (each is a nullable <c>int32</c>; verified against
/// <c>GET /v1/machines/{MachineID}/machineProducts</c> in the Nayax developer documentation) - and
/// leaves <see cref="MachineStockObservationPolicy"/> to decide what that means. Deliberately not
/// the <c>(PAR - MissingStockByMDB) ?? 0</c> coercion the machine product list and the Pick List
/// apply: a zero that actually means "Nayax did not say" would read as an empty machine and invent a
/// shrinkage.
///
/// A slot with no Nayax product id is dropped, because nothing can be reconciled against an
/// unidentified selection.
///
/// Translating the identity is part of that translation: a slot's <c>NayaxProductID</c> is resolved
/// to a local product id through <see cref="INayaxProductLinkStore"/> (issue #733), because
/// <see cref="MachineStockObservationSlot.ProductId"/> is the local product the ledger's refills,
/// sales, counts and names are keyed by. A slot whose Nayax product this business holds no link for
/// is dropped for the same reason an unidentified one is - it names no product of ours - which is
/// exactly what already happened to a slot whose product was not in the caller's catalogue.
/// </summary>
public sealed class NayaxMachineStockObservationSource : IMachineStockObservationSource
{
    private readonly INayaxLynxClient _nayax;
    private readonly INayaxProductLinkStore _productLinks;
    private readonly IClock _clock;

    public NayaxMachineStockObservationSource(
        INayaxLynxClient nayax, INayaxProductLinkStore productLinks, IClock clock)
    {
        _nayax = nayax;
        _productLinks = productLinks;
        _clock = clock;
    }

    public async Task<MachineStockObservation> GetObservationAsync(
        long machineId, CancellationToken cancellationToken)
    {
        // Fresh, never the cached selections (issue #842): an observation is the machine's own
        // statement of what it is holding at this instant, and the ledger compares it with the
        // expected level to report a shrinkage or an overage. A minute-old figure here would move
        // that difference.
        var slots = await _nayax.GetMachineProductsFreshAsync(machineId, cancellationToken);
        var observedAtUtc = _clock.UtcNow;

        var identifiedSlots = slots.Where(slot => slot.NayaxProductID.HasValue).ToList();
        var localProductIdByNayaxId = await _productLinks.GetLocalProductIdsAsync(
            identifiedSlots.Select(slot => slot.NayaxProductID!.Value).Distinct().ToList(), cancellationToken);

        return new MachineStockObservation(
            observedAtUtc,
            identifiedSlots
                .Where(slot => localProductIdByNayaxId.ContainsKey(slot.NayaxProductID!.Value))
                .Select(slot => new MachineStockObservationSlot(
                    localProductIdByNayaxId[slot.NayaxProductID!.Value], slot.PAR, slot.MissingStockByMDB))
                .ToList());
    }
}
