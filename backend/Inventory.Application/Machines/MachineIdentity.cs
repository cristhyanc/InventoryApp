namespace Inventory.Application.Machines;

/// <summary>
/// One machine of the caller's business, named both ways (issue #741, parent #329 child G2).
///
/// <para><see cref="LocalMachineId"/> is <c>Machines.Id</c>, this application's own key: since
/// #741 it is what <c>StockAdjustment.MachineId</c> and <c>MachineStockCount.MachineId</c> hold and
/// what every <c>api/machines/{id}</c> route takes. <see cref="NayaxMachineId"/> is the number Nayax
/// knows the same machine by, read from <c>LocalMachine.ExternalMachineId</c> through
/// <see cref="Nayax.INayaxMachineLinkStore"/>, and it is what the provider's own data still holds:
/// the live fleet, <c>NayaxSales.MachineID</c>, <c>NayaxMachineStockEvent.MachineId</c> and the
/// inventory-cost transition's recorded opening machine stock. It is <c>null</c> for a machine the
/// business created itself, and for every machine of a business that does not sell through
/// Nayax.</para>
///
/// <para>A feature that touches both kinds of data carries this pair rather than one number, so a
/// local id is never handed to the provider and a provider number is never stored as a local
/// reference.</para>
/// </summary>
public sealed record MachineIdentity(long LocalMachineId, long? NayaxMachineId)
{
    /// <summary>
    /// The provider's number, for a feature that cannot work without it (the live fleet, Sync
    /// Restock). The routes that reach such a feature answer 404 for a machine without one, so
    /// arriving here without it is a programming error rather than a state of the business.
    /// </summary>
    public long RequiredNayaxMachineId =>
        NayaxMachineId ?? throw new InvalidOperationException(
            "This machine has no Nayax machine number, so a Nayax-only feature cannot run for it.");
}
