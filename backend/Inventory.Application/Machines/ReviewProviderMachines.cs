using Inventory.Application.Nayax;
using Inventory.Application.Time;
using Inventory.Domain.Machines;
using Inventory.Domain.Providers;

namespace Inventory.Application.Machines;

/// <summary>
/// Reads the business's provider fleet and reconciles it against the business's own machines (issue
/// #742, parent #329 child G3): the one place a provider machine becomes - or waits to become - a
/// local machine.
///
/// <para><strong>Two outcomes, and which one happens is not a preference.</strong> A provider machine
/// no local machine carries the id of is created as a new local machine <em>automatically</em> when
/// the business has no machine of its own without a provider id, and waits in a review list when it
/// has. The rule is <see cref="ProviderMachineLinkPolicy.MayLinkAutomatically"/>: with nothing
/// unlinked locally there is no machine the new one could be a duplicate of, and with something
/// unlinked every one of them might be, so the operator decides.</para>
///
/// <para><strong>Nothing is matched by name.</strong> A local machine called exactly what the provider
/// calls its machine is a different machine and stays unlinked - the one link this use case makes
/// without being asked is a machine it creates itself, which is a row with no history to get
/// wrong.</para>
///
/// <para><strong>The provider read comes first, and the transaction after it.</strong> The same shape
/// <c>StartCatalogueReview</c> uses: a failed fleet read propagates and nothing is written, so a
/// partly-read fleet is never treated as the whole of it, and no provider request is made while a
/// database transaction is held open. The eligibility read, the decision and the write are then one
/// transaction, so a machine recorded by hand between them cannot be duplicated by an auto-link that
/// never saw it.</para>
///
/// <para><strong>It links machines and nothing else.</strong> No sale, stock movement, machine count
/// or report is touched: until a provider machine is linked its provider data stays under its raw
/// provider number, exactly as before, and it keeps appearing on every provider-only page.</para>
/// </summary>
public sealed class ReviewProviderMachines
{
    private readonly INayaxLynxClient _nayax;
    private readonly IProviderMachineLinkStore _store;
    private readonly IClock _clock;

    public ReviewProviderMachines(INayaxLynxClient nayax, IProviderMachineLinkStore store, IClock clock)
    {
        _nayax = nayax;
        _store = store;
        _clock = clock;
    }

    public async Task<ProviderMachineReview> Handle(CancellationToken cancellationToken = default)
    {
        var fleet = await _nayax.GetMachinesAsync(cancellationToken);

        await using var transaction = await _store.BeginTransactionAsync(cancellationToken);
        var local = await _store.ListLocalMachinesAsync(cancellationToken);

        var localByProviderId = local
            .Where(machine => machine.ExternalMachineId is not null)
            .ToDictionary(machine => machine.ExternalMachineId!, machine => machine, StringComparer.Ordinal);

        var linked = new List<ProviderMachineEntry>();
        var unlinked = new List<ProviderMachineEntry>();
        foreach (var entry in Entries(fleet))
        {
            var providerId = NayaxExternalId.From(entry.ProviderMachineId);
            if (localByProviderId.TryGetValue(providerId, out var carrier))
                linked.Add(entry with { LocalMachineId = carrier.MachineId, LocalMachineName = carrier.Name });
            else
                unlinked.Add(entry);
        }

        // Read inside the same transaction as the write it permits, because it is a statement about
        // stored rows: a machine the operator recorded while the fleet was being read must stop the
        // auto-link rather than be duplicated by it.
        var mayLinkAutomatically = ProviderMachineLinkPolicy.MayLinkAutomatically(
            local.Any(machine => machine.ExternalMachineId is null));

        if (unlinked.Count == 0 || !mayLinkAutomatically)
            return new ProviderMachineReview(_clock.UtcNow, [], unlinked, linked);

        var written = await _store.LinkAsync(
            unlinked
                .Select(entry => new ProviderMachineLinkCommand(
                    NayaxExternalId.From(entry.ProviderMachineId),
                    entry.Name,
                    ProviderMachineLinkAction.CreateNew,
                    LocalMachineId: null))
                .ToList(),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var autoLinked = new List<ProviderMachineLinked>();
        foreach (var entry in unlinked)
        {
            var providerId = NayaxExternalId.From(entry.ProviderMachineId);
            var result = written.Single(link =>
                string.Equals(link.ExternalMachineId, providerId, StringComparison.Ordinal));

            autoLinked.Add(new ProviderMachineLinked(
                entry.ProviderMachineId, result.LocalMachineId, result.Name, result.Created));
            linked.Add(entry with { LocalMachineId = result.LocalMachineId, LocalMachineName = result.Name });
        }

        return new ProviderMachineReview(_clock.UtcNow, autoLinked, [], linked);
    }

    /// <summary>
    /// One entry per machine in the fleet, deduplicated by provider machine id and named the way a
    /// machine created for it would be.
    ///
    /// <para>A machine with no identifier is left out. The authoritative Lynx contract for <c>GET
    /// /v1/machines</c> declares <c>MachineID</c> as <c>integer&lt;int64&gt; | null</c> (Nayax
    /// developer portal, Get All Machine Basic Info), so <c>0</c> is the absent value this
    /// application's non-nullable field records rather than a machine - the same exclusion the
    /// <c>AddMachines</c> backfill makes, for the same reason.</para>
    /// </summary>
    private static List<ProviderMachineEntry> Entries(IReadOnlyList<NayaxMachine> fleet)
    {
        var entries = new List<ProviderMachineEntry>();
        var seen = new HashSet<long>();

        foreach (var machine in fleet)
        {
            if (machine.MachineID == 0 || !seen.Add(machine.MachineID))
                continue;

            entries.Add(new ProviderMachineEntry(
                machine.MachineID,
                ProviderMachineLinkPolicy.LinkedMachineName(
                    machine.MachineName, NayaxExternalId.From(machine.MachineID)),
                machine.MachineNumber,
                LocalMachineId: null,
                LocalMachineName: null));
        }

        return entries;
    }
}
