using Inventory.Domain.MachineStock;

namespace Inventory.Application.MachineStock;

/// <summary>
/// What the machine itself reports it is holding, at the instant it was read.
/// <see cref="Slots"/> is one entry per machine selection, carrying the source values unchanged -
/// including their absence - so the Domain decides what is observable and what is not.
/// </summary>
public sealed record MachineStockObservation(DateTime ObservedAtUtc, IReadOnlyList<MachineStockObservationSlot> Slots);

/// <summary>
/// Narrow port for the machine's own stock report (issue #56), owned by the Application layer. The
/// adapter is <c>Inventory.Infrastructure.Nayax.NayaxMachineStockObservationSource</c>, which reads
/// the live Nayax machine-product slots; nothing here knows that, so the ledger's observed side can
/// come from another source later without touching the use case or the Domain.
/// </summary>
public interface IMachineStockObservationSource
{
    /// <summary>
    /// The machine's reported slots. A machine that reports no selection at all yields an
    /// observation with no slots, which is a different answer from "the source could not be read" -
    /// the latter is reported by throwing the source's own failure, which the use case translates
    /// into a data-quality state only for the two conditions that mean this business has no usable
    /// connection at all.
    /// </summary>
    Task<MachineStockObservation> GetObservationAsync(long machineId, CancellationToken cancellationToken);
}
