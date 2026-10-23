using Inventory.Application.Machines;
using Inventory.Application.Nayax;
using Inventory.Application.Time;
using Inventory.Domain.MachineStock;

namespace Inventory.Application.MachineStock;

/// <summary>
/// The machine-stock reconciliation read (issue #56): what one machine is expected to be holding,
/// what it reports it is holding, and the difference between the two.
///
/// It orchestrates and decides nothing itself. The movement semantics, the replay, the observation
/// arithmetic and the comparison all belong to <c>Inventory.Domain.MachineStock</c>; this use case
/// reads the machine's own report through <see cref="IMachineStockObservationSource"/>, reads the
/// persisted facts through <see cref="IMachineStockLedgerStore"/>, and hands them over.
///
/// Two source conditions are translated into data quality rather than into a failed request, because
/// both of them say "this business has no usable Nayax connection", which is a state the report
/// should still be readable in: <see cref="NayaxNotConnectedException"/> and
/// <see cref="NayaxPermissionNotGrantedException"/>. Expected stock, refills, sales and counts are
/// local facts and remain fully available. Every other failure - a transport error, or the
/// support-session refusal that blocks Nayax entirely - propagates unchanged, so a real outage is
/// never reported as an empty machine.
/// </summary>
public sealed class GetMachineStockReconciliation
{
    private readonly IMachineStockLedgerStore _store;
    private readonly IMachineStockObservationSource _observations;
    private readonly IClock _clock;

    public GetMachineStockReconciliation(
        IMachineStockLedgerStore store, IMachineStockObservationSource observations, IClock clock)
    {
        _store = store;
        _observations = observations;
        _clock = clock;
    }

    public async Task<MachineStockReconciliationDto> Handle(MachineIdentity machine, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(machine);

        MachineStockObservation? observation;
        MachineStockDataQualityIssue? sourceIssue = null;
        try
        {
            // The machine's own stock report is the provider's, read by its Nayax number. A machine
            // with none - one the business created itself (issue #741) - has no report to read, which
            // is the same "nothing observed" state an unreachable provider is.
            observation = machine.NayaxMachineId is { } nayaxMachineId
                ? await _observations.GetObservationAsync(nayaxMachineId, cancellationToken)
                : null;
            if (observation is null)
                sourceIssue = SourceUnavailable();
        }
        catch (NayaxNotConnectedException)
        {
            observation = null;
            sourceIssue = SourceUnavailable();
        }
        catch (NayaxPermissionNotGrantedException)
        {
            observation = null;
            sourceIssue = SourceUnavailable();
        }

        var observedSlots = observation?.Slots ?? [];
        var facts = await _store.GetLedgerFactsAsync(
            machine,
            observedSlots.Select(slot => slot.ProductId).Distinct().ToList(),
            cancellationToken);

        var ledger = MachineStockLedger.Project(
            new MachineStockLedgerInputs(facts.Refills, facts.Sales, facts.Counts, facts.OpeningBaselines),
            facts.ProductNames);

        // An observed slot whose product is not in this business's catalogue is dropped rather than
        // reported: the catalogue read is tenant-scoped, so "not found" is the ownership boundary and
        // an unmapped machine selection at the same time - the same exclusion the Pick List applies.
        // A slot whose Nayax product this business holds no link for never arrives at all: since
        // issue #733 the observation source resolves that identity through the link table and drops
        // what it cannot resolve, rather than offering a remote id as a local product id.
        // The second check is the same rule for a projected row; the store resolves a name for every
        // product it returns facts about, so it only ever excludes a row there is nothing to label.
        var rows = MachineStockReconciliationPolicy
            .ReconcileMachine(
                ledger,
                observedSlots.Where(slot => facts.ProductNames.ContainsKey(slot.ProductId)).ToList(),
                facts.ProductNames)
            .Where(row => facts.ProductNames.ContainsKey(row.ProductId))
            .OrderBy(row => facts.ProductNames[row.ProductId], StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.ProductId)
            .Select(row => ToRow(facts.ProductNames[row.ProductId], row))
            .ToList();

        return new MachineStockReconciliationDto(
            machine.LocalMachineId,
            _clock.UtcNow,
            observation?.ObservedAtUtc,
            rows,
            MachineIssues(sourceIssue, facts));
    }

    private static MachineStockProductReconciliationDto ToRow(
        string productName, MachineStockProductReconciliation reconciliation) =>
        new(
            reconciliation.ProductId,
            productName,
            reconciliation.ExpectedQuantity,
            reconciliation.ObservedQuantity,
            reconciliation.DiscrepancyQuantity,
            reconciliation.ShrinkageQuantity,
            reconciliation.OverageQuantity,
            reconciliation.Status,
            reconciliation.BaselineSource,
            reconciliation.BaselineAtUtc,
            reconciliation.BaselineQuantity,
            reconciliation.RefilledQuantity,
            reconciliation.ReturnedToStorageQuantity,
            reconciliation.SoldQuantity,
            reconciliation.UnexplainedChangeAtLastCount,
            reconciliation.Issues);

    private static MachineStockDataQualityIssue SourceUnavailable() => new(
        MachineStockDataQualityIssueCodes.ObservationSourceUnavailable,
        "The machine's own stock report could not be read, so nothing observed is available to "
            + "reconcile against. Expected stock is still derived from this machine's refills, sales "
            + "and counts.");

    private static List<MachineStockDataQualityIssue> MachineIssues(
        MachineStockDataQualityIssue? sourceIssue, MachineStockLedgerFacts facts)
    {
        var issues = new List<MachineStockDataQualityIssue>();
        if (sourceIssue is not null)
            issues.Add(sourceIssue);

        if (facts.PendingNayaxStockEventCount > 0)
        {
            issues.Add(new MachineStockDataQualityIssue(
                MachineStockDataQualityIssueCodes.PendingNayaxStockEvents,
                $"{facts.PendingNayaxStockEventCount} Nayax stock event(s) for this machine are still "
                    + "unprocessed in Sync Restock, so a refill this machine has already received may be "
                    + "missing from the ledger and a shortage here may not be shrinkage."));
        }

        if (facts.UnattributedCompletedSaleCount > 0)
        {
            issues.Add(new MachineStockDataQualityIssue(
                MachineStockDataQualityIssueCodes.UnattributedCompletedSales,
                $"{facts.UnattributedCompletedSaleCount} completed sale(s) on this machine could not be "
                    + "matched to a catalogue product, so the units they dispensed are not deducted from "
                    + "any product's expected stock."));
        }

        return issues;
    }
}
