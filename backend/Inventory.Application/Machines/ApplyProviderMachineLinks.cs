using Inventory.Application.Nayax;
using Inventory.Application.Time;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Machines;
using Inventory.Domain.Providers;

namespace Inventory.Application.Machines;

/// <summary>
/// Carries out the operator's decisions about the provider machines a review left awaiting one
/// (issue #742, parent #329 child G3), and only ever exactly those.
///
/// <para><strong>Every provider value comes from the server's own fleet read.</strong> The request
/// names a provider machine and, for a link, a machine of the business; the provider's name for the
/// machine is read here, in this operation, so a created machine is named what the provider calls it
/// rather than what a browser said it calls it. A decision naming a provider machine this business's
/// fleet does not contain is refused.</para>
///
/// <para><strong>Atomic.</strong> One refused decision refuses all of them: nothing is linked and
/// nothing is created. A half-applied review is the outcome an operator cannot reason about, because
/// the links it did write are the ones the next review treats as settled.</para>
///
/// <para><strong>The authoritative read, the comparison and the write are one operation.</strong>
/// Whether a machine already carries a provider id, and whether another machine already carries this
/// one, are statements about stored rows, so they are decided inside the write's own transaction by
/// the store (AGENTS.md § Automated task contract, on stale-state guarantees).</para>
///
/// <para><strong>Nothing is matched by name</strong>, here either: a link is the operator's explicit
/// choice of one of their machines, or a machine this apply creates.</para>
/// </summary>
public sealed class ApplyProviderMachineLinks
{
    /// <summary>
    /// The caller-safe refusal for a decision about a provider machine that is not in this business's
    /// fleet. It is refused rather than ignored: a fleet this application cannot see the machine in is
    /// not one it may record a provider id from, and an apply that silently dropped the decision would
    /// report success for a link that was never made.
    /// </summary>
    public static string UnknownProviderMachineMessage(long providerMachineId) =>
        $"Provider machine {providerMachineId} is not in this business's provider fleet, so it cannot "
            + "be linked. Nothing was changed.";

    private readonly INayaxLynxClient _nayax;
    private readonly IProviderMachineLinkStore _store;
    private readonly IClock _clock;

    public ApplyProviderMachineLinks(
        INayaxLynxClient nayax, IProviderMachineLinkStore store, IClock clock)
    {
        _nayax = nayax;
        _store = store;
        _clock = clock;
    }

    public async Task<ProviderMachineLinksApplied> Handle(
        ApplyProviderMachineLinksRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Read before the transaction is opened, like every other provider-then-database operation
        // here: it is where the provider's own name for each machine comes from, and a failed read
        // leaves the machines exactly as they are.
        // Fresh, never the cached list (issue #840): a link is refused for a provider machine that is
        // not in the fleet, so the fleet this decision is checked against has to be the current one.
        var fleet = await _nayax.GetMachinesFreshAsync(cancellationToken);
        var byProviderMachineId = Fleet(fleet);

        var planned = Planned(request.Decisions ?? [], byProviderMachineId);
        if (planned.Count == 0)
            return new ProviderMachineLinksApplied(_clock.UtcNow, []);

        await using var transaction = await _store.BeginTransactionAsync(cancellationToken);
        var written = await _store.LinkAsync(
            planned.Select(link => link.Command).ToList(), cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        // Matched back by the stored provider id text this operation itself supplied, so the
        // provider's own machine number never has to be parsed out of a column.
        var providerMachineIdByExternalId = planned.ToDictionary(
            link => link.Command.ExternalMachineId, link => link.ProviderMachineId, StringComparer.Ordinal);

        return new ProviderMachineLinksApplied(
            _clock.UtcNow,
            written
                .Select(link => new ProviderMachineLinked(
                    providerMachineIdByExternalId[link.ExternalMachineId],
                    link.LocalMachineId,
                    link.Name,
                    link.Created))
                .ToList());
    }

    /// <summary>One link this apply will record, with the provider machine number it is about.</summary>
    private sealed record PlannedLink(long ProviderMachineId, ProviderMachineLinkCommand Command);

    /// <summary>
    /// The fleet by provider machine id, keeping the first entry for a repeated id and leaving out a
    /// machine with no identifier - the same two exclusions <see cref="ReviewProviderMachines"/>
    /// makes, because the two have to agree about which provider machines exist.
    /// </summary>
    private static Dictionary<long, NayaxMachine> Fleet(IReadOnlyList<NayaxMachine> fleet)
    {
        var byProviderMachineId = new Dictionary<long, NayaxMachine>();
        foreach (var machine in fleet.Where(machine => machine.MachineID != 0))
            byProviderMachineId.TryAdd(machine.MachineID, machine);

        return byProviderMachineId;
    }

    /// <summary>
    /// Validates the submitted decisions against the fleet and turns them into the links to record.
    /// These are the refusals that are statements about the <em>decision</em> - a provider machine
    /// that is not in the fleet, a duplicate, a missing or surplus local machine - so they are
    /// answered before anything local is read. Every refusal is raised, and raised with the whole
    /// list, because an apply that silently dropped the one decision it could not carry out would
    /// report success for a link the operator never got.
    /// </summary>
    private static List<PlannedLink> Planned(
        IReadOnlyList<ProviderMachineLinkDecision> submitted,
        IReadOnlyDictionary<long, NayaxMachine> fleet)
    {
        var refusals = new List<string>();
        var planned = new List<PlannedLink>();
        var seen = new HashSet<long>();

        foreach (var decision in submitted)
        {
            if (!fleet.TryGetValue(decision.ProviderMachineId, out var machine))
            {
                refusals.Add(UnknownProviderMachineMessage(decision.ProviderMachineId));
                continue;
            }

            if (!seen.Add(decision.ProviderMachineId))
            {
                refusals.Add(
                    $"Provider machine {decision.ProviderMachineId} was decided more than once.");
                continue;
            }

            if (ProviderMachineLinkPolicy.DecisionRefusal(decision.Action, decision.LocalMachineId)
                is { } refusal)
            {
                refusals.Add($"Provider machine {decision.ProviderMachineId}: {refusal}");
                continue;
            }

            if (decision.Action == ProviderMachineLinkAction.Defer)
                continue;

            var providerId = NayaxExternalId.From(decision.ProviderMachineId);
            planned.Add(new PlannedLink(
                decision.ProviderMachineId,
                new ProviderMachineLinkCommand(
                    providerId,
                    ProviderMachineLinkPolicy.LinkedMachineName(machine.MachineName, providerId),
                    decision.Action,
                    decision.LocalMachineId)));
        }

        if (refusals.Count > 0)
            throw new DomainValidationException("Nothing was applied. " + string.Join(" ", refusals));

        return planned;
    }
}
