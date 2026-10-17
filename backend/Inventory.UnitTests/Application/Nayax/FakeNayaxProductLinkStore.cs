using Inventory.Application.Nayax;

namespace InventoryApi.Tests.Application.Nayax;

/// <summary>
/// A deterministic <see cref="INayaxProductLinkStore"/> (issue #733) over an explicit set of Nayax
/// product links, so a test states exactly which Nayax ids name which local products.
///
/// <para><see cref="EveryIdLinkedToItself"/> is the shape issue #732's backfill recorded for data
/// that existed before the link table did - every Nayax id linked to the local product with the
/// same id - and is what tests written before this change are asserting against. A test that is
/// about the lookup itself uses <see cref="Linking"/> with ids that differ, because a store that
/// mapped an unknown id to itself would hide the very mistake this port exists to prevent.</para>
/// </summary>
public sealed class FakeNayaxProductLinkStore : INayaxProductLinkStore
{
    private readonly Dictionary<long, long>? _localProductIdByNayaxId;

    private FakeNayaxProductLinkStore(Dictionary<long, long>? localProductIdByNayaxId)
    {
        _localProductIdByNayaxId = localProductIdByNayaxId;
    }

    /// <summary>The Nayax product ids each call was asked to resolve, in call order.</summary>
    public List<IReadOnlyCollection<long>> Requests { get; } = [];

    /// <summary>Only the given links exist; every other Nayax id resolves to nothing.</summary>
    public static FakeNayaxProductLinkStore Linking(params (long NayaxProductId, long LocalProductId)[] links) =>
        new(links.ToDictionary(link => link.NayaxProductId, link => link.LocalProductId));

    /// <summary>Every Nayax id is linked to the local product whose id is the same number.</summary>
    public static FakeNayaxProductLinkStore EveryIdLinkedToItself() => new(null);

    public Task<IReadOnlyDictionary<long, long>> GetLocalProductIdsAsync(
        IReadOnlyCollection<long> nayaxProductIds, CancellationToken cancellationToken)
    {
        Requests.Add(nayaxProductIds.ToList());

        var resolved = nayaxProductIds
            .Distinct()
            .Where(nayaxProductId => _localProductIdByNayaxId is null
                || _localProductIdByNayaxId.ContainsKey(nayaxProductId))
            .ToDictionary(
                nayaxProductId => nayaxProductId,
                nayaxProductId => _localProductIdByNayaxId is null
                    ? nayaxProductId
                    : _localProductIdByNayaxId[nayaxProductId]);

        return Task.FromResult<IReadOnlyDictionary<long, long>>(resolved);
    }
}
