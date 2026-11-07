using Inventory.Application.NayaxReconciliation;
using Inventory.Domain.NayaxSqs;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="INayaxSaleProviderEvidenceStore"/> (issue #1104): the
/// reads the hourly reconciliation compares over, and the append-only evidence of what it saw.
///
/// <para><strong>Every write is one <c>BEGIN IMMEDIATE</c> transaction that re-reads the
/// sale.</strong> That is the whole correctness argument, and it is the one issue #1105 depends on
/// too. A reconciliation run reads a sale's state, decides a transition from it and then writes - and
/// an Owner decision (#1105) will do exactly the same thing to the same row. Without the write lock
/// taken <em>before</em> the read, both read the old state and the loser silently overwrites a
/// decision the winner committed in between; with it the second writer waits, re-reads the committed
/// state and decides from that. So the authoritative read, the transition comparison and the mutation
/// are one operation rather than three, and the evidence row and the state it caused commit together
/// or not at all: a state no evidence supports, and evidence with no state beside it, are both
/// impossible.</para>
///
/// <para><strong>A tracking query is not an authoritative read, which is why that read reloads the
/// row.</strong> The whole run - the save of issue #1103 and then this comparison - happens in one
/// scoped <see cref="AppDbContext"/>, and <see cref="EfLatestNayaxSalesStore"/> has already loaded
/// and tracked every sale the response spoke about. EF Core answers a later query for a key it
/// already tracks with the instance it is already tracking and does <em>not</em> refresh that
/// instance's values from the row the query returned, so a query alone would decide this write from
/// values read minutes earlier - before this lock existed - which is exactly the race the lock is
/// taken to prevent. Reloading inside the transaction replaces the tracked values <em>and</em> their
/// original values with the committed row, so the comparison, the transition and the <c>UPDATE</c>
/// this method emits are all decided from it. There is nothing unsaved for the reload to discard:
/// both of <see cref="EfLatestNayaxSalesStore"/>'s save paths commit before returning.</para>
///
/// <para><strong>The comparison is decided here, not handed in.</strong> The five field results come
/// from <see cref="NayaxSaleProviderComparisonPolicy"/> applied to the values that same authoritative
/// read returned, for the reason the transition is decided here: a result computed before the lock
/// was taken is a result about a sale that may since have changed. The policy stays the one place a
/// comparison is made (AGENTS.md § Sales, payment methods, and statuses); only the moment it is
/// applied belongs to this transaction.</para>
///
/// <para><strong>Nothing here takes a business id.</strong> Every statement runs in the service scope
/// resolved to the one business being reconciled, so the central tenant query filters find the rows
/// and <see cref="BusinessOwnershipEnforcer"/> stamps and checks the insert. There is no
/// <c>IgnoreQueryFilters</c>, no raw SQL and no unscoped context.</para>
///
/// <para><strong>The provider's values are stored exactly as received.</strong> No rounding, no
/// trimming, no upper-casing: the normalisations belong to
/// <see cref="NayaxSaleProviderComparisonPolicy"/> and are recorded as the per-field results beside
/// them. An evidence row has to say what the provider sent, or it is evidence about this
/// application's own arithmetic instead.</para>
///
/// <para><strong>A repeated identical observation writes nothing.</strong> An hourly run re-reads the
/// same rolling window, so most of what it sees it has already seen; a row is appended only when this
/// run changed the sale's state, or when the observation differs from that sale's latest evidence of
/// the same kind. The comparison is made against the stored row inside the same transaction, which is
/// what stops two runs both deciding "this is new".</para>
/// </summary>
public sealed class EfNayaxSaleProviderEvidenceStore : INayaxSaleProviderEvidenceStore
{
    private readonly AppDbContext _db;

    public EfNayaxSaleProviderEvidenceStore(AppDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<NayaxVerifiableSale>> FindQueueSalesByTransactionIdsAsync(
        IReadOnlyCollection<long> transactionIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transactionIds);

        if (transactionIds.Count == 0)
        {
            return [];
        }

        var ids = transactionIds as ICollection<long> ?? transactionIds.ToList();

        return await QueueSales()
            .Where(sale => ids.Contains(sale.TransactionID))
            .Select(Projection())
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<NayaxVerifiableSale>> FindQueueSalesBracketedByAsync(
        long machineId,
        DateTime earliestExclusiveUtc,
        DateTime latestExclusiveUtc,
        CancellationToken cancellationToken) =>
        await QueueSales()
            .Where(sale => sale.MachineID == machineId
                && sale.MachineAuthorizationTime > earliestExclusiveUtc
                && sale.MachineAuthorizationTime < latestExclusiveUtc)
            .Select(Projection())
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public Task<NayaxSaleEvidenceWriteResult> RecordComparisonAsync(
        NayaxSaleComparisonEvidence evidence, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        return RecordAsync(
            new EvidenceWriteContext(evidence.SaleId, evidence.RunId, evidence.ObservedAtUtc),
            NayaxSaleProviderEvidenceKind.Comparison,
            sale => ComparisonOf(evidence.Provider, sale),
            cancellationToken);
    }

    /// <summary>
    /// What comparing the provider's item with <paramref name="sale"/> - the sale this write's own
    /// authoritative read returned - observed: the state it is evidence for, whether it repeats the
    /// sale's latest comparison, and the row it would append.
    /// </summary>
    private static EvidenceObservation ComparisonOf(
        NayaxProviderSaleValues provider, NayaxSales sale)
    {
        var comparison = NayaxSaleProviderComparisonPolicy.Compare(StoredValuesOf(sale), provider);

        return new EvidenceObservation(
            comparison.ObservedState,
            // The observation this kind of row carries: what the provider sent, and what each field's
            // comparison answered. A later run that sees the same two things has learnt nothing.
            latest => ComparisonObservation(latest)
                == new ComparisonObservationOf(provider, comparison.Fields),
            row =>
            {
                row.ProviderMachineId = provider.MachineId;
                row.ProviderSettlementValue = provider.SettlementValue;
                row.ProviderAuthorizationInstantUtc = provider.AuthorizationInstantUtc;
                row.ProviderCurrencyCode = provider.CurrencyCode;
                row.ProviderNayaxProductId = provider.NayaxProductId;
                row.ProviderProductName = provider.ProductName;
                row.MachineComparison = comparison.Fields.Machine;
                row.AmountComparison = comparison.Fields.Amount;
                row.InstantComparison = comparison.Fields.Instant;
                row.CurrencyComparison = comparison.Fields.Currency;
                row.ProductComparison = comparison.Fields.Product;
            });
    }

    /// <inheritdoc />
    public Task<NayaxSaleEvidenceWriteResult> RecordAbsenceAsync(
        NayaxSaleAbsenceEvidence evidence, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        return RecordAsync(
            new EvidenceWriteContext(evidence.SaleId, evidence.RunId, evidence.ObservedAtUtc),
            NayaxSaleProviderEvidenceKind.Absence,
            _ => new EvidenceObservation(
                evidence.Observation.ObservedState,
                latest => AbsenceObservation(latest)
                    == new AbsenceObservationOf(
                        evidence.Window,
                        evidence.SaleAuthorizationInstantUtc,
                        evidence.SaleReceivedViaSqsAtUtc,
                        evidence.Observation.Bracketed,
                        evidence.Observation.OutsideReceiptGrace),
                row =>
                {
                    row.ReadMachineId = evidence.Window.MachineId;
                    row.ReturnedItemCount = evidence.Window.ReturnedItemCount;
                    row.ReturnedEarliestInstantUtc = evidence.Window.EarliestReturnedInstantUtc;
                    row.ReturnedLatestInstantUtc = evidence.Window.LatestReturnedInstantUtc;
                    row.SaleAuthorizationInstantUtc = evidence.SaleAuthorizationInstantUtc;
                    row.SaleReceivedViaSqsAtUtc = evidence.SaleReceivedViaSqsAtUtc;
                    row.Bracketed = evidence.Observation.Bracketed;
                    row.OutsideReceiptGrace = evidence.Observation.OutsideReceiptGrace;
                }),
            cancellationToken);
    }

    /// <summary>
    /// The identity of one evidence write - which sale, which run, and when it was observed - the
    /// three values every evidence row carries regardless of its kind, exactly as
    /// <see cref="NayaxSaleComparisonEvidence"/> and <see cref="NayaxSaleAbsenceEvidence"/> already
    /// carry them.
    /// </summary>
    private readonly record struct EvidenceWriteContext(long SaleId, Guid RunId, DateTime ObservedAtUtc);

    /// <summary>
    /// What one write learnt about a sale, every part of it decided from the authoritative read:
    /// which state the observation is evidence for, whether it repeats that sale's latest row of the
    /// same kind, and how it fills the row it would append.
    /// </summary>
    private sealed record EvidenceObservation(
        NayaxSqsVerification? ObservedState,
        Func<NayaxSaleProviderEvidence?, bool> RepeatsLatest,
        Action<NayaxSaleProviderEvidence> Fill);

    /// <summary>
    /// The one write path, shared by both kinds: take SQLite's write lock, re-read the sale
    /// authoritatively, decide the observation from <em>that</em> sale, apply whatever transition it
    /// permits, decide whether a row is worth appending, and commit the row and the state together.
    /// </summary>
    private async Task<NayaxSaleEvidenceWriteResult> RecordAsync(
        EvidenceWriteContext context,
        NayaxSaleProviderEvidenceKind kind,
        Func<NayaxSales, EvidenceObservation> observe,
        CancellationToken cancellationToken)
    {
        // Before the reads, not at the first write: a deferred transaction would take the lock after
        // the state this transition is decided from had already been read, which is exactly the race
        // issue #1105's Owner decisions make real.
        await using var transaction = await SqliteWriteLockTransaction
            .BeginAsync(_db, cancellationToken)
            .ConfigureAwait(false);

        var sale = await AuthoritativeSaleAsync(context.SaleId, cancellationToken)
            .ConfigureAwait(false);

        if (sale?.SqsVerification is not { } current)
        {
            // The sale is gone, or it does not carry a queue-delivery state at all. Either way there
            // is nothing this reconciliation may say about it, and nothing is recorded.
            return new NayaxSaleEvidenceWriteResult(false, null, sale?.SqsVerification, false);
        }

        // Only now, and only from the row this transaction just read: the comparison's own inputs
        // are as much a part of the decision as the state it is applied to.
        var observation = observe(sale);

        var next = NayaxSqsVerificationTransitions.Apply(current, observation.ObservedState);
        var stateChanged = next != current;

        var latest = await _db.NayaxSaleProviderEvidences
            .AsNoTracking()
            .Where(row => row.NayaxSaleId == context.SaleId && row.Kind == kind)
            .OrderByDescending(row => row.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!stateChanged && observation.RepeatsLatest(latest))
        {
            // Nothing learnt and nothing changed. An hourly run re-reading the same rolling window
            // would otherwise append an identical row every hour for every sale in it.
            return new NayaxSaleEvidenceWriteResult(false, null, current, false);
        }

        var evidence = new NayaxSaleProviderEvidence
        {
            NayaxSaleId = context.SaleId,
            RunId = context.RunId,
            ObservedAtUtc = context.ObservedAtUtc,
            Kind = kind,

            // The resulting state, which is not always the state the observation argued for: matching
            // evidence about a conflicting row is still recorded, and says truthfully that the sale
            // stayed conflicting.
            ResultingState = next ?? current,

            // BusinessId is deliberately left unset: BusinessOwnershipEnforcer stamps it from the
            // resolved scope on SaveChanges, exactly as it does for every other tenant-owned insert,
            // so this adapter never assigns an owner itself.
        };

        observation.Fill(evidence);
        _db.NayaxSaleProviderEvidences.Add(evidence);

        if (stateChanged)
        {
            sale.SqsVerification = next;
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new NayaxSaleEvidenceWriteResult(true, evidence.Id, next, stateChanged);
    }

    /// <summary>
    /// The sale as it is committed <em>now</em>, inside this transaction's write lock, or
    /// <see langword="null"/> when this business has no such row.
    ///
    /// <para>It is two steps because one would not be authoritative. The query is the ordinary
    /// tenant-filtered, tracking one - the central filter is what finds the row, and the state change
    /// goes through <c>SaveChanges</c> with <see cref="BusinessOwnershipEnforcer"/> - but EF Core
    /// resolves it to an instance this context is already tracking, with the values that earlier read
    /// saw, whenever an earlier step of the same run already loaded the row (which
    /// <see cref="EfLatestNayaxSalesStore"/> does for every sale the response spoke about). The
    /// reload is what makes the tracked instance, and its original values, the committed row.</para>
    /// </summary>
    private async Task<NayaxSales?> AuthoritativeSaleAsync(
        long saleId, CancellationToken cancellationToken)
    {
        var sale = await _db.NayaxSales
            .FirstOrDefaultAsync(row => row.Id == saleId, cancellationToken)
            .ConfigureAwait(false);

        if (sale is null)
        {
            return null;
        }

        var entry = _db.Entry(sale);
        await entry.ReloadAsync(cancellationToken).ConfigureAwait(false);

        // A reload detaches an entity whose row is gone, which is the one answer the query above
        // cannot give on its own: it would have returned the tracked instance of a row somebody
        // deleted before this lock was taken.
        return entry.State is EntityState.Detached ? null : sale;
    }

    /// <summary>
    /// The sale's own side of the comparison, read from the authoritative row: the same five values
    /// <see cref="Projection"/> reads, including the currency that is always <see langword="null"/>
    /// because no sale column holds one. The two must stay in step: this one decides what is
    /// compared, <see cref="Projection"/> decides what the use case reads back.
    /// </summary>
    private static NayaxStoredSaleValues StoredValuesOf(NayaxSales sale) => new(
        sale.MachineID,
        sale.SettlementValue,
        sale.MachineAuthorizationTime,
        null,
        sale.NayaxProductId);

    /// <summary>
    /// This business's queue-delivered sales: the rows that carry an <c>SqsVerification</c> value at
    /// all. A sale the <c>lastSales</c> sync or an uploaded export imported has no queue delivery to
    /// verify, so it is never read here and never given a state.
    /// </summary>
    private IQueryable<NayaxSales> QueueSales() =>
        _db.NayaxSales.AsNoTracking().Where(sale => sale.SqsVerification != null);

    /// <summary>
    /// What the comparison reads from a stored sale, and nothing more. The currency is
    /// <see langword="null"/> because no sale column holds one - which the comparison records as
    /// "not compared" rather than as agreement (see <see cref="NayaxStoredSaleValues"/>).
    /// </summary>
    private static System.Linq.Expressions.Expression<Func<NayaxSales, NayaxVerifiableSale>> Projection() =>
        sale => new NayaxVerifiableSale(
            sale.Id,
            sale.TransactionID,
            sale.SqsVerification!.Value,
            sale.ReceivedViaSqsAtUtc,
            new NayaxStoredSaleValues(
                sale.MachineID,
                sale.SettlementValue,
                sale.MachineAuthorizationTime,
                null,
                sale.NayaxProductId));

    /// <summary>
    /// What a comparison row recorded, as one comparable value. Deduplication asks "is this the same
    /// thing we saw last time?", and one value comparison cannot quietly miss a field the way a list
    /// of hand-written checks can.
    /// </summary>
    private sealed record ComparisonObservationOf(
        NayaxProviderSaleValues Provider, NayaxSaleFieldComparisons Fields);

    /// <summary>The same, for an absence row.</summary>
    private sealed record AbsenceObservationOf(
        NayaxProviderMachineWindow Window,
        DateTime? SaleAuthorizationInstantUtc,
        DateTime? SaleReceivedViaSqsAtUtc,
        bool Bracketed,
        bool OutsideReceiptGrace);

    /// <summary>
    /// A stored comparison row read back as the observation it recorded, or <see langword="null"/>
    /// when there is no such row - or when one is missing a value this kind of row always writes,
    /// which is treated as "not the same observation" rather than as a match.
    /// </summary>
    private static ComparisonObservationOf? ComparisonObservation(NayaxSaleProviderEvidence? row) =>
        row is null
            || row.ProviderMachineId is not { } machineId
            || row.ProviderSettlementValue is not { } settlementValue
            || row.MachineComparison is not { } machine
            || row.AmountComparison is not { } amount
            || row.InstantComparison is not { } instant
            || row.CurrencyComparison is not { } currency
            || row.ProductComparison is not { } product
                ? null
                : new ComparisonObservationOf(
                    new NayaxProviderSaleValues(
                        machineId,
                        settlementValue,
                        row.ProviderAuthorizationInstantUtc,
                        row.ProviderCurrencyCode,
                        row.ProviderNayaxProductId,
                        row.ProviderProductName),
                    new NayaxSaleFieldComparisons(machine, amount, instant, currency, product));

    /// <summary>The same, for an absence row.</summary>
    private static AbsenceObservationOf? AbsenceObservation(NayaxSaleProviderEvidence? row) =>
        row is null
            || row.ReadMachineId is not { } machineId
            || row.ReturnedItemCount is not { } itemCount
            || row.Bracketed is not { } bracketed
            || row.OutsideReceiptGrace is not { } outsideGrace
                ? null
                : new AbsenceObservationOf(
                    new NayaxProviderMachineWindow(
                        machineId,
                        itemCount,
                        row.ReturnedEarliestInstantUtc,
                        row.ReturnedLatestInstantUtc),
                    row.SaleAuthorizationInstantUtc,
                    row.SaleReceivedViaSqsAtUtc,
                    bracketed,
                    outsideGrace);
}
