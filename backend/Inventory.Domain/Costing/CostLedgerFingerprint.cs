using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Inventory.Domain.Costing;

/// <summary>
/// A deterministic fingerprint of everything <see cref="WeightedAverageCostReplay"/> consumes for
/// one product (issue #359).
///
/// It is what makes the costing-repair apply refuse a stale preview, the way
/// <c>ApplyInventoryCostTransition</c> refuses one: a preview reports the fingerprint of the ledger
/// it projected, and the apply recomputes it from its own authoritative read inside the same
/// transaction as the write. If a purchase, count, refill, sale or earlier repair landed in
/// between, the projection the operator approved is no longer the one that would be applied, so the
/// apply stops instead of writing a repair against a history it never saw.
///
/// The transition compares a small fixed state record field by field; a cost ledger is an
/// unbounded list of movements, sales and repairs, so this compares a hash of a canonical rendering
/// of them instead. Canonical means: a fixed field order, every collection sorted by its own key,
/// timestamps as ticks (so a stored <see cref="DateTimeKind.Unspecified"/> value and the same
/// instant as UTC are one ledger - the replay compares ticks alone) and decimals without
/// insignificant trailing zeros (so a re-read value of a different scale is not mistaken for a
/// change).
/// </summary>
public static class CostLedgerFingerprint
{
    /// <summary>
    /// Bumping this invalidates every outstanding preview, which is the intended effect whenever
    /// the rendering below changes: a fingerprint from an older rendering must never be treated as
    /// matching a newer one.
    /// </summary>
    private const string Version = "v1";

    private const char FieldSeparator = '|';
    private const char RecordSeparator = '\n';

    /// <summary>The fingerprint as lowercase hexadecimal SHA-256.</summary>
    public static string Compute(
        CostReplayProduct product,
        IReadOnlyCollection<CostReplayAdjustment> adjustments,
        IReadOnlyCollection<CostReplaySale> sales,
        IReadOnlyCollection<CostReplayRepair> repairs,
        CostReplayBaseline? baseline)
    {
        ArgumentNullException.ThrowIfNull(product);
        ArgumentNullException.ThrowIfNull(adjustments);
        ArgumentNullException.ThrowIfNull(sales);
        ArgumentNullException.ThrowIfNull(repairs);

        const string Absent = "none";
        var canonical = new StringBuilder();
        Append(canonical, Version, Number(product.ProductId), Number(product.QuantityInStock),
            Optional(product.CostingQuantity), Money(product.InventoryValue));
        Append(canonical, "baseline", baseline is null ? Absent : Number(baseline.CutoffAt.Ticks),
            baseline is null ? Absent : Number(baseline.HomeStockQuantity),
            baseline is null ? Absent : Number(baseline.OpeningCostingQuantity),
            baseline is null ? Absent : Money(baseline.InventoryValue));

        Append(canonical, "adjustments", Number(adjustments.Count));
        foreach (var adjustment in adjustments.OrderBy(x => x.Id))
        {
            Append(canonical, "a", Number(adjustment.Id), Number(adjustment.EffectiveAt.Ticks),
                Number((int)adjustment.Reason), Number(adjustment.QuantityChange), Money(adjustment.UnitCost),
                adjustment.HasReceiptItemLink ? "linked" : "unlinked");
        }

        Append(canonical, "sales", Number(sales.Count));
        foreach (var sale in sales.OrderBy(x => x.TransactionId))
            Append(canonical, "s", Number(sale.TransactionId), Number(sale.AuthorizationTime.Ticks));

        Append(canonical, "repairs", Number(repairs.Count));
        foreach (var repair in repairs.OrderBy(x => x.Id))
        {
            Append(canonical, "r", Number(repair.Id), Number(repair.EffectiveAt.Ticks),
                Number(repair.Quantity), Money(repair.UnitCost));
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static void Append(StringBuilder canonical, params string[] fields) =>
        canonical.AppendJoin(FieldSeparator, fields).Append(RecordSeparator);

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Optional(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "none";

    /// <summary>
    /// A decimal without insignificant trailing zeros, so <c>1.5</c> and <c>1.500000</c> - the same
    /// value stored at a different scale - render identically.
    /// </summary>
    private static string Money(decimal? value)
    {
        if (value is not { } number)
            return "none";

        var text = number.ToString(CultureInfo.InvariantCulture);
        return text.Contains('.', StringComparison.Ordinal)
            ? text.TrimEnd('0').TrimEnd('.')
            : text;
    }
}
