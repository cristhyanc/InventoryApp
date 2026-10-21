namespace Inventory.Application.Nayax;

/// <summary>
/// The one way a Nayax machine identifier becomes a local machine identifier (issue #780, parent
/// #329 child G2a) - the machine half of <see cref="INayaxProductLinkStore"/>, with the same rules.
///
/// <para><strong>A Nayax id is not a local id.</strong> A machine is a row this application owns
/// since issue #740, keyed by <c>Machines.Id</c>, and the provider's own number for it lives in
/// <c>LocalMachine.ExternalMachineId</c> as opaque text. A feature that holds a Nayax machine number
/// - because it read the live fleet, or because it is reading stored provider sales - resolves it to
/// a local machine through this port, so the translation happens once, at the edge where Nayax data
/// enters, and a local id equal to a Nayax machine number stays the coincidence it is.</para>
///
/// <para><strong>No recorded Nayax id means no machine.</strong> A number this business has recorded
/// on no machine names nothing locally, and is absent from the result rather than mapped to itself.
/// A business whose sales provider is not Nayax - including one with no provider at all - resolves
/// nothing here either, for the same reason it resolves no product: the column holds whatever
/// provider that business sells through, and does not say who issued the value.</para>
/// </summary>
public interface INayaxMachineLinkStore
{
    /// <summary>
    /// The local machine id for each of the given Nayax machine numbers the current business has
    /// recorded, keyed by the Nayax number. Numbers it has not recorded are absent from the result,
    /// and an empty request reads nothing.
    ///
    /// <para>The result is scoped to the caller's business by the ordinary tenant query filters, and
    /// a provider id is stored on the machine row itself, so a Nayax number another business uses
    /// resolves to nothing here.</para>
    /// </summary>
    Task<IReadOnlyDictionary<long, long>> GetLocalMachineIdsAsync(
        IReadOnlyCollection<long> nayaxMachineIds, CancellationToken cancellationToken);

    /// <summary>
    /// The Nayax machine number the current business has recorded for local machine
    /// <paramref name="localMachineId"/>, or <c>null</c> when that machine carries none - a machine
    /// the business created itself - when no such machine is visible, or when the business does not
    /// sell through Nayax (issue #741). The reverse of <see cref="GetLocalMachineIdsAsync"/>, with
    /// the same rules: a stored id this application could not have written is not a Nayax number
    /// and resolves to nothing.
    /// </summary>
    Task<long?> GetNayaxMachineIdAsync(long localMachineId, CancellationToken cancellationToken);
}
