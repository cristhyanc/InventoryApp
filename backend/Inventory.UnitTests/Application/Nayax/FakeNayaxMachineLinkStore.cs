using Inventory.Application.Nayax;

namespace InventoryApi.Tests.Application.Nayax;

/// <summary>
/// A deterministic <see cref="INayaxMachineLinkStore"/> (issue #780) over an explicit set of Nayax
/// machine links, so a test states exactly which provider machine numbers name which local machines.
/// The machine half of <see cref="FakeNayaxProductLinkStore"/>, with the same two shapes.
///
/// <para><see cref="NothingLinked"/> is the state of a business that has recorded no machine for the
/// numbers the provider reports - a fleet read answered before anything created one - and is what a
/// test asserting "no local machine" uses. A test that is about the lookup itself uses
/// <see cref="Linking"/> with numbers that differ from the local ids, because a store that mapped a
/// number to itself would hide the very mistake this port exists to prevent.</para>
/// </summary>
public sealed class FakeNayaxMachineLinkStore : INayaxMachineLinkStore
{
    private readonly Dictionary<long, long> _localMachineIdByNayaxId;

    private FakeNayaxMachineLinkStore(Dictionary<long, long> localMachineIdByNayaxId)
    {
        _localMachineIdByNayaxId = localMachineIdByNayaxId;
    }

    /// <summary>The Nayax machine numbers each call was asked to resolve, in call order.</summary>
    public List<IReadOnlyCollection<long>> Requests { get; } = [];

    /// <summary>Only the given links exist; every other Nayax machine number resolves to nothing.</summary>
    public static FakeNayaxMachineLinkStore Linking(params (long NayaxMachineId, long LocalMachineId)[] links) =>
        new(links.ToDictionary(link => link.NayaxMachineId, link => link.LocalMachineId));

    /// <summary>No machine number names a local machine at all.</summary>
    public static FakeNayaxMachineLinkStore NothingLinked() => new([]);

    public Task<IReadOnlyDictionary<long, long>> GetLocalMachineIdsAsync(
        IReadOnlyCollection<long> nayaxMachineIds, CancellationToken cancellationToken)
    {
        Requests.Add(nayaxMachineIds.ToList());

        var resolved = nayaxMachineIds
            .Distinct()
            .Where(_localMachineIdByNayaxId.ContainsKey)
            .ToDictionary(
                nayaxMachineId => nayaxMachineId,
                nayaxMachineId => _localMachineIdByNayaxId[nayaxMachineId]);

        return Task.FromResult<IReadOnlyDictionary<long, long>>(resolved);
    }

    /// <summary>The local machine ids each reverse lookup was asked for, in call order.</summary>
    public List<long> ReverseRequests { get; } = [];

    public Task<long?> GetNayaxMachineIdAsync(long localMachineId, CancellationToken cancellationToken)
    {
        ReverseRequests.Add(localMachineId);

        var nayaxMachineIds = _localMachineIdByNayaxId
            .Where(link => link.Value == localMachineId)
            .Select(link => (long?)link.Key)
            .ToList();

        return Task.FromResult(nayaxMachineIds.Count == 1 ? nayaxMachineIds[0] : null);
    }
}
