using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Inventory.Domain.SupplierOrders;

/// <summary>
/// A deterministic fingerprint of everything the "complete this order with undelivered quantities"
/// decision was shown over (issue #974): the owning business, the order, its fulfillment status, and
/// every line's ordered and received quantity.
///
/// It is what makes the purchase save refuse a stale or foreign decision, the way
/// <c>Inventory.Domain.Gst.HistoricalGstClassificationFingerprint</c> and
/// <c>Inventory.Domain.Costing.CostLedgerFingerprint</c> already do for their own previews: the
/// preview reports the fingerprint of the order it projected, and the purchase-save transaction
/// recomputes it from its own authoritative read of that order inside the same transaction as the
/// write. A competing receipt, a cancellation, a completion or an edited order landing in between
/// changes it, so the balance the operator decided over is no longer the balance that would be
/// closed and the save stops instead of closing one nobody saw.
///
/// The owning business id is part of the rendering, which is what binds a decision to its tenant.
/// It is never supplied by a caller: the Application resolves it from the order row the tenant query
/// filters already matched.
///
/// Canonical means a fixed field order, lines sorted by their own id, and decimals rendered without
/// insignificant trailing zeros so a re-read value of a different scale is not mistaken for a
/// change. The fingerprint is not a secret and does not need to be: it is only ever compared against
/// a value the server recomputes, and a caller that submits anything else is refused. Nothing a
/// caller submits is written - not a quantity, not a receipt total, not a status - so there is
/// nothing for a tampered value to influence beyond being rejected.
/// </summary>
public static class SupplierOrderCompletionFingerprint
{
    /// <summary>
    /// Bumping this invalidates every outstanding decision, which is the intended effect whenever
    /// the rendering below changes: a fingerprint from an older rendering must never be treated as
    /// matching a newer one.
    /// </summary>
    private const string Version = "v1";

    private const char FieldSeparator = '|';
    private const char RecordSeparator = '\n';

    /// <summary>The fingerprint as lowercase hexadecimal SHA-256.</summary>
    public static string Compute(
        int businessId,
        int supplierOrderId,
        SupplierOrderStatus status,
        IEnumerable<SupplierOrderLineFulfillment> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var ordered = lines.OrderBy(line => line.LineId).ToList();
        var canonical = new StringBuilder();
        Append(canonical, Version, Number(businessId), Number(supplierOrderId), Number((int)status), Number(ordered.Count));

        foreach (var line in ordered)
        {
            Append(
                canonical,
                "l",
                Number(line.LineId),
                Quantity(line.QuantityOrdered),
                Quantity(line.QuantityReceived));
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static void Append(StringBuilder canonical, params string[] fields) =>
        canonical.AppendJoin(FieldSeparator, fields).Append(RecordSeparator);

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// A quantity without insignificant trailing zeros, so <c>6</c> and <c>6.0000</c> - the same
    /// value stored at the column's <c>decimal(18,4)</c> scale - render identically.
    /// </summary>
    private static string Quantity(decimal value)
    {
        var text = value.ToString(CultureInfo.InvariantCulture);
        return text.Contains('.', StringComparison.Ordinal)
            ? text.TrimEnd('0').TrimEnd('.')
            : text;
    }
}
