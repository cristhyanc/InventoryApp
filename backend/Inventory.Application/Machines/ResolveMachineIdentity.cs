using Inventory.Application.Nayax;

namespace Inventory.Application.Machines;

/// <summary>
/// Turns the local machine id an <c>api/machines/{id}</c> route receives into the
/// <see cref="MachineIdentity"/> the features behind it need (issue #741).
///
/// <para><strong>The route id is a local machine.</strong> A value that is not a machine of the
/// caller's business - another business's machine, or a provider machine number sent by an older
/// client - finds nothing, because <see cref="ILocalMachineStore"/> reads through the ordinary
/// tenant query filters, and the route answers 404. A provider number is never tried as a fallback:
/// a local id that happens to equal a Nayax machine number names a different machine.</para>
///
/// <para><strong>The provider's number is looked up, never assumed.</strong> It comes from
/// <see cref="INayaxMachineLinkStore"/>, which answers only while the business sells through Nayax,
/// so a machine the business created itself, or any machine of a business with no provider, comes
/// back with no Nayax number.</para>
/// </summary>
public sealed class ResolveMachineIdentity
{
    private readonly ILocalMachineStore _machines;
    private readonly INayaxMachineLinkStore _machineLinks;

    public ResolveMachineIdentity(ILocalMachineStore machines, INayaxMachineLinkStore machineLinks)
    {
        _machines = machines;
        _machineLinks = machineLinks;
    }

    /// <summary>
    /// The machine <paramref name="localMachineId"/> names in the caller's business, with its Nayax
    /// number when it has one, or <c>null</c> when the business has no such machine.
    /// </summary>
    public async Task<MachineIdentity?> Handle(long localMachineId, CancellationToken cancellationToken)
    {
        if (await _machines.FindByIdAsync(localMachineId, cancellationToken) is null)
            return null;

        var nayaxMachineId = await _machineLinks.GetNayaxMachineIdAsync(localMachineId, cancellationToken);
        return new MachineIdentity(localMachineId, nayaxMachineId);
    }
}
