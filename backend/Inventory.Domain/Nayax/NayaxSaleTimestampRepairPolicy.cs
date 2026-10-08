using Inventory.Domain.Exceptions;

namespace Inventory.Domain.Nayax;

/// <summary>
/// Where an authoritative Nayax authorization instant came from (issue #472). It is recorded on
/// every applied repair, because a change to a financial record's instant has to stay attributable
/// to the source that justified it. The numeric values mirror the persisted enum.
/// </summary>
public enum NayaxSaleTimestampEvidenceSource
{
    /// <summary>
    /// The live Lynx rolling window, <c>GET /v1/machines/{MachineID}/lastSales</c>, read through
    /// <c>INayaxLynxClient</c>. It covers only the transactions the window still returns.
    /// </summary>
    NayaxLastSalesApi = 1,

    /// <summary>
    /// An operator-supplied Nayax transaction export carrying the <c>AuthorizationDateTimeGMT</c>
    /// column, read through the existing export reader. This is the only supported source for a
    /// transaction older than the rolling window, because the Lynx API publishes no date-ranged
    /// sales endpoint that carries the authorization instant.
    /// </summary>
    OperatorExport = 2,
}

/// <summary>What the repair decided about one stored sale.</summary>
public enum NayaxSaleTimestampRepairOutcome
{
    /// <summary>Source evidence names a different authoritative instant; the sale would be moved.</summary>
    Repairable,

    /// <summary>Source evidence names exactly the instant already stored; nothing to write.</summary>
    AlreadyCorrect,

    /// <summary>Nothing verified enough to act on. The sale is left exactly as it is.</summary>
    Unresolved,
}

/// <summary>
/// Why a stored sale was left unresolved. Each of these is a refusal to guess, never a silent
/// skip: the preview reports the row and the operator decides what evidence to obtain next.
/// </summary>
public enum NayaxSaleTimestampUnresolvedReason
{
    /// <summary>No source covered this transaction - typically older than the rolling window.</summary>
    NoSourceEvidence,

    /// <summary>The source covered it but its authorization value could not be read as an instant.</summary>
    UnreadableEvidence,

    /// <summary>Two sources named different authorization instants for the same transaction.</summary>
    ConflictingEvidence,

    /// <summary>The evidence names another machine, so it is evidence about another sale.</summary>
    MachineMismatch,

    /// <summary>The evidence's settled amount does not identify the stored sale.</summary>
    AmountMismatch,
}

/// <summary>
/// One stored Nayax sale, as the repair reads it. Everything here is a raw persisted fact; the
/// product is carried as the remote identifier and the reported name, because matching it to a
/// local product is the <c>ProductMatcher</c>'s decision and not this policy's.
/// </summary>
public sealed record StoredNayaxSale(
    long TransactionId,
    long MachineId,
    string? MachineName,
    decimal SettlementValue,
    int? TransactionStatusId,
    DateTime StoredInstantUtc,
    long? NayaxProductId,
    string? ProductName);

/// <summary>
/// One piece of authoritative source evidence about one transaction's authorization instant.
///
/// <paramref name="AuthorizationInstantUtc"/> is <c>null</c> when the source covered the transaction
/// but carried no readable <c>AuthorizationDateTimeGMT</c> value. That is deliberately not the same
/// as no evidence at all: a source that claimed an authoritative instant and could not be read is a
/// data-quality fact the operator has to see, and it never falls back to a machine-local clock, an
/// update time or an arithmetic shift (AGENTS.md § Nayax integration, docs/architecture.md § Nayax
/// sale timestamps).
/// </summary>
/// <param name="TransactionId">The remote Nayax transaction identifier the evidence is about.</param>
/// <param name="MachineId">The remote Nayax machine identifier the source reported for it.</param>
/// <param name="AuthorizationInstantUtc">
/// The authoritative <c>AuthorizationDateTimeGMT</c> value normalized to a true UTC instant, or
/// <c>null</c> when the source carried no readable one.
/// </param>
/// <param name="SettlementValue">
/// The settled amount the source reported, when it reported one; used only to verify that the
/// evidence identifies the stored sale.
/// </param>
/// <param name="Source">Which supported path the value was read from.</param>
/// <param name="Reference">
/// Caller-safe provenance text naming the read this evidence came from - a window read instant or an
/// uploaded file name - recorded on the applied repair. Never a credential, a card number or a
/// filesystem path.
/// </param>
public sealed record NayaxSaleTimestampEvidence(
    long TransactionId,
    long MachineId,
    DateTime? AuthorizationInstantUtc,
    decimal? SettlementValue,
    NayaxSaleTimestampEvidenceSource Source,
    string Reference);

/// <summary>
/// What the policy decided about one stored sale: the outcome, the instant it would be moved to
/// when that outcome is <see cref="NayaxSaleTimestampRepairOutcome.Repairable"/>, the reason when it
/// is <see cref="NayaxSaleTimestampRepairOutcome.Unresolved"/>, and the evidence that decided it.
/// </summary>
public sealed record NayaxSaleTimestampDecision(
    StoredNayaxSale Sale,
    NayaxSaleTimestampRepairOutcome Outcome,
    NayaxSaleTimestampUnresolvedReason? UnresolvedReason,
    DateTime? RepairedInstantUtc,
    NayaxSaleTimestampEvidence? Evidence);

/// <summary>
/// The deterministic rules of the Nayax sale timestamp repair (issue #472).
///
/// Two populations of stored rows hold an instant that was never the authoritative one - rows
/// ingested before issue #380, which hold machine-local wall-clock ticks, and rows the live
/// synchronization stored between issues #380 and #471 from an offset-free
/// <c>AuthorizationDateTimeGMT</c> value, which hold an instant shifted by the Sydney host's own UTC
/// offset. Neither fix repaired them, and the latest-sales synchronization never rewrites a stored
/// instant, so a normal refresh cannot repair history either.
///
/// This policy is how such a row is repaired, and the shape of it is the point:
/// <list type="bullet">
///   <item><b>Per transaction, from its own evidence.</b> The affected population is mixed - 28 of
///   152 matched records already held the authoritative instant while 124 were shifted - so there is
///   no business-wide difference to apply. A blanket <c>+11</c>, <c>+22</c> or any other global
///   offset is prohibited, and nothing here subtracts one stored value from another to derive one.</item>
///   <item><b>Identity is verified, not assumed.</b> A remote <c>TransactionID</c> is unique only
///   within the operator account that issued it, so evidence must agree with the stored sale's
///   machine and settled amount before it may move it. (The owning business is not checked here: the
///   caller only ever reads its own sales, through the <c>AppDbContext</c> tenant query filters.)</item>
///   <item><b>Absent, unreadable and conflicting evidence are refusals.</b> Each leaves the sale
///   exactly as it is and is reported as explicitly unresolved, because inventing an instant
///   misplaces revenue between Sydney business days permanently, while an unresolved row can be
///   repaired later once the operator obtains the source for it.</item>
///   <item><b>Re-deciding after a repair changes nothing.</b> The evidence then matches the stored
///   instant, so the row is <see cref="NayaxSaleTimestampRepairOutcome.AlreadyCorrect"/> and the
///   apply writes nothing - which is what makes applying the same verified repair again a no-op.</item>
/// </list>
/// Business dates, revenue movement between Sydney periods and the affected products' costing
/// ranges are derived from these decisions by the Application layer, which owns the
/// <c>Australia/Sydney</c> calendar port; this type stays free of time zones and of persistence.
/// </summary>
public static class NayaxSaleTimestampRepairPolicy
{
    /// <summary>
    /// How long a stored preview may be applied after it was created. Longer than the inventory-cost
    /// transition's thirty minutes because a repair preview is a per-transaction list an operator has
    /// to review against an export, and a plan that goes stale in the meantime is <em>detected</em>
    /// by <see cref="EnsureStoredSalesUnchanged"/> rather than trusted.
    /// </summary>
    public static readonly TimeSpan PreviewLifetime = TimeSpan.FromHours(2);

    /// <summary>
    /// The tolerance within which an evidence amount still identifies the stored sale, matching the
    /// repository's established one-cent reconciliation tolerance. It is an identity check only:
    /// nothing in a repair ever writes an amount.
    /// </summary>
    public const decimal AmountIdentityTolerance = 0.01m;

    /// <summary>The caller-safe refusal when the database moved underneath a preview.</summary>
    public const string StalePlanMessage =
        "The stored Nayax sales changed after this preview, so nothing was repaired. "
        + "Run the preview again and review it before applying.";

    /// <summary>
    /// Decides every stored sale against the evidence collected for it, in the order the sales were
    /// read. Evidence for a transaction the caller's business does not hold decides nothing at all -
    /// it is a missing sale, not a timestamp defect, and is reported separately.
    /// </summary>
    public static IReadOnlyList<NayaxSaleTimestampDecision> Decide(
        IReadOnlyCollection<StoredNayaxSale> sales,
        IReadOnlyCollection<NayaxSaleTimestampEvidence> evidence)
    {
        ArgumentNullException.ThrowIfNull(sales);
        ArgumentNullException.ThrowIfNull(evidence);

        var byTransaction = evidence
            .GroupBy(item => item.TransactionId)
            .ToDictionary(group => group.Key, group => group.ToArray());

        var decisions = new List<NayaxSaleTimestampDecision>(sales.Count);
        foreach (var sale in sales)
            decisions.Add(Decide(sale, byTransaction.GetValueOrDefault(sale.TransactionId, [])));
        return decisions;
    }

    private static NayaxSaleTimestampDecision Decide(
        StoredNayaxSale sale,
        NayaxSaleTimestampEvidence[] evidence)
    {
        if (evidence.Length == 0)
            return Unresolved(sale, NayaxSaleTimestampUnresolvedReason.NoSourceEvidence, null);

        // Two sources are allowed to repeat the same instant - the rolling window and an export
        // normally do - but they are never averaged, voted on or ordered by recency. Any
        // disagreement, including one source reading the value and another failing to, is the
        // operator's to resolve.
        var first = evidence[0];
        if (evidence.Any(item => item.AuthorizationInstantUtc != first.AuthorizationInstantUtc))
            return Unresolved(sale, NayaxSaleTimestampUnresolvedReason.ConflictingEvidence, first);
        if (evidence.FirstOrDefault(item => item.MachineId != sale.MachineId) is { } foreignMachine)
            return Unresolved(sale, NayaxSaleTimestampUnresolvedReason.MachineMismatch, foreignMachine);
        if (evidence.FirstOrDefault(item =>
                item.SettlementValue is { } amount
                && Math.Abs(amount - sale.SettlementValue) > AmountIdentityTolerance) is { } foreignAmount)
            return Unresolved(sale, NayaxSaleTimestampUnresolvedReason.AmountMismatch, foreignAmount);
        if (first.AuthorizationInstantUtc is not { } authoritative)
            return Unresolved(sale, NayaxSaleTimestampUnresolvedReason.UnreadableEvidence, first);

        return authoritative == sale.StoredInstantUtc
            ? new(sale, NayaxSaleTimestampRepairOutcome.AlreadyCorrect, null, null, first)
            : new(sale, NayaxSaleTimestampRepairOutcome.Repairable, null, authoritative, first);
    }

    private static NayaxSaleTimestampDecision Unresolved(
        StoredNayaxSale sale,
        NayaxSaleTimestampUnresolvedReason reason,
        NayaxSaleTimestampEvidence? evidence) =>
        new(sale, NayaxSaleTimestampRepairOutcome.Unresolved, reason, null, evidence);

    /// <summary>
    /// The earliest instant each repairable sale would be read from, old or new. A date change must
    /// never leave stale COGS behind: a sale moving later leaves its old position, where the replay
    /// has to restart, and a sale moving earlier arrives before costed sales that now follow it, so
    /// the rebuild starts at whichever of the two instants comes first.
    /// </summary>
    public static DateTime EarliestAffectedInstant(NayaxSaleTimestampDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);

        return decision.RepairedInstantUtc is { } repaired && repaired < decision.Sale.StoredInstantUtc
            ? repaired
            : decision.Sale.StoredInstantUtc;
    }

    /// <summary>A stored preview may be applied once, and only before it expires.</summary>
    public static void EnsureDraftUsable(DateTime? appliedAt, DateTime expiresAt, DateTime now)
    {
        if (appliedAt.HasValue)
            throw new DomainValidationException("This sale timestamp repair preview has already been applied.");
        if (expiresAt < now)
            throw new DomainValidationException(
                "The sale timestamp repair preview expired. Run the preview again and review it before applying.");
    }

    /// <summary>
    /// Refuses a plan whose stored sales are no longer the ones it was computed from: the same
    /// transactions, each with every stored fact the plan was derived from unchanged - instant,
    /// machine, settled amount, status and product mapping alike. The authoritative read, this
    /// comparison and the write are one operation in the apply, so a sale imported, re-timed,
    /// restatused, rematched or already repaired in between cannot be repaired against a plan nobody
    /// approved.
    ///
    /// The comparison is order-independent, because the plan is a set of transactions rather than a
    /// query result whose ordering is part of the contract.
    /// </summary>
    public static void EnsureStoredSalesUnchanged(
        IReadOnlyCollection<StoredNayaxSale> previewed,
        IReadOnlyCollection<StoredNayaxSale> current)
    {
        ArgumentNullException.ThrowIfNull(previewed);
        ArgumentNullException.ThrowIfNull(current);

        if (previewed.Count != current.Count)
            throw new DomainValidationException(StalePlanMessage);

        // Indexing rather than adding keeps the loop body a plain statement and the enumeration free
        // of side-effecting predicates: a repeated remote transaction id simply collapses, so the
        // dictionary ends up smaller than the collection it was built from. A duplicate means this is
        // not the set the plan was computed from, which is the stale-plan refusal.
        var currentByTransaction = new Dictionary<long, StoredNayaxSale>(current.Count);
        foreach (var sale in current)
            currentByTransaction[sale.TransactionId] = sale;

        if (currentByTransaction.Count != current.Count)
            throw new DomainValidationException(StalePlanMessage);

        foreach (var sale in previewed)
            if (!currentByTransaction.TryGetValue(sale.TransactionId, out var now) || now != sale)
                throw new DomainValidationException(StalePlanMessage);
    }
}
