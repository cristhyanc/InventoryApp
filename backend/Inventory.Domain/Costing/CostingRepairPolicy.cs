using Inventory.Domain.Exceptions;

namespace Inventory.Domain.Costing;

/// <summary>A costing repair whose values have passed <see cref="CostingRepairPolicy.Validate"/>.</summary>
public sealed record ValidatedCostingRepair(DateTime EffectiveAt, int Quantity, decimal UnitCost, string Reason)
{
    /// <summary>The inventory value the repair adds: <c>quantity * unit cost</c>, unrounded.</summary>
    public decimal TotalValue => Quantity * UnitCost;
}

/// <summary>
/// The deterministic rules of a costing-only historical repair (issue #359).
///
/// A repair exists for one situation: physical stock is known to have existed, but the costing
/// history that would have valued it is incomplete, so the weighted-average replay reports
/// <c>UnknownCost</c>/<c>MissingOpening</c> and every later write to the product fails on it.
/// Restock, Correction, Damaged, Expired and MachineRefill are physical movements and keep their
/// meanings, so none of them can express that; a repair is the explicit, auditable, costing-only
/// alternative.
///
/// What it is deliberately not: it is never inferred (not from machine-refill gaps, not from Nayax
/// events, never from the product's current cost), never negative, and never a general
/// inventory-value journal entry. Only a positive acquisition at a stated cost, with a reason a
/// human can audit later, is accepted. Every check throws a caller-safe
/// <see cref="DomainValidationException"/>.
/// </summary>
public static class CostingRepairPolicy
{
    /// <summary>
    /// How much reason text is required. A costing repair rewrites historical COGS, so the record
    /// has to explain itself to whoever reads it in a year's time. A length floor plus the
    /// placeholder list below is a deliberately blunt instrument: it cannot judge whether a reason
    /// is true, only refuse the ones that say nothing at all.
    /// </summary>
    public const int MinimumReasonLength = 10;

    /// <summary>
    /// Reason texts that are long enough to pass <see cref="MinimumReasonLength"/> but still say
    /// nothing. Shorter placeholders ("n/a", "fix", "none", "test") are already refused by length.
    /// </summary>
    private static readonly HashSet<string> GenericReasons = new(StringComparer.OrdinalIgnoreCase)
    {
        "not applicable",
        "no reason",
        "no comment",
        "costing repair",
        "cost repair",
        "stock repair",
        "repair stock",
        "data repair",
        "adjustment",
        "correction",
        "missing stock",
        "missing cost",
        "unknown",
        "placeholder",
    };

    /// <summary>
    /// Validates the repair a caller asked for and returns it normalised: the effective time as the
    /// UTC instant it names, and the reason trimmed.
    /// </summary>
    /// <exception cref="DomainValidationException">
    /// The quantity is not positive, the unit cost is negative, or the reason is empty or generic.
    /// </exception>
    public static ValidatedCostingRepair Validate(DateTime effectiveAt, int quantity, decimal unitCost, string? reason)
    {
        if (quantity <= 0)
            throw new DomainValidationException("A costing repair must add a positive quantity.");
        if (unitCost < 0)
            throw new DomainValidationException("A costing repair unit cost cannot be negative.");

        var trimmedReason = reason?.Trim() ?? string.Empty;
        if (trimmedReason.Length < MinimumReasonLength || GenericReasons.Contains(trimmedReason))
            throw new DomainValidationException(
                $"Record a specific reason for this costing repair: at least {MinimumReasonLength} characters, and not a placeholder.");

        return new(NormalizeEffectiveAt(effectiveAt), quantity, unitCost, trimmedReason);
    }

    /// <summary>
    /// A repair's effective time is stored as a UTC instant, like every stock movement's
    /// <c>EffectiveAt</c>. A local time names an instant, so it is converted; an unspecified one is
    /// read as UTC, which is how the same value materialises when SQLite hands a stored timestamp
    /// back (see the AppDbContext comments on <c>DateTimeKind</c>).
    /// </summary>
    public static DateTime NormalizeEffectiveAt(DateTime effectiveAt) =>
        effectiveAt.Kind == DateTimeKind.Local
            ? effectiveAt.ToUniversalTime()
            : DateTime.SpecifyKind(effectiveAt, DateTimeKind.Utc);

    /// <summary>
    /// A repair must take effect strictly after the product's inventory-cost transition baseline
    /// cutoff, when it has one. The replay ignores every event at or before the cutoff, so a repair
    /// placed there would be accepted, persisted and then silently do nothing.
    /// </summary>
    /// <exception cref="DomainValidationException">The repair is at or before the cutoff.</exception>
    public static void EnsureAfterBaselineCutoff(DateTime effectiveAt, DateTime? cutoffAt)
    {
        if (cutoffAt is { } cutoff && NormalizeEffectiveAt(effectiveAt).Ticks <= cutoff.Ticks)
            throw new DomainValidationException(
                $"A costing repair must take effect after the product's inventory-cost transition cutoff "
                    + $"({cutoff:yyyy-MM-dd HH:mm:ss}Z). The cost replay ignores everything at or before the cutoff.");
    }

    /// <summary>
    /// A repair meant to cost a sale must replay before it. The check is delegated to
    /// <see cref="WeightedAverageCostReplay.ReplaysBefore"/> rather than comparing the two
    /// timestamps here, because a repair's effective time and a Nayax sale's authorization time are
    /// not recorded in the same time zone (an existing mismatch, not fixed here): the only
    /// trustworthy question is where the replay itself puts them.
    /// </summary>
    /// <exception cref="DomainValidationException">The repair does not replay before the sale.</exception>
    public static void EnsureReplaysBeforeSale(CostReplayRepair repair, CostReplaySale sale)
    {
        ArgumentNullException.ThrowIfNull(repair);
        ArgumentNullException.ThrowIfNull(sale);

        if (!WeightedAverageCostReplay.ReplaysBefore(repair, sale))
            throw new DomainValidationException(
                $"The repair does not replay before completed sale {sale.TransactionId}, so it cannot cost that sale. "
                    + "Choose an earlier effective time and preview again.");
    }
}
