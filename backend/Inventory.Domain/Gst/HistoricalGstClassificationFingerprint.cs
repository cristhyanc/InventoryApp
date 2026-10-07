using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Inventory.Domain.Gst;

/// <summary>
/// A deterministic fingerprint of everything the historical GST classification plan is derived from
/// (issue #433): the owning business, every stored purchase component, and every configured rule
/// that could classify one.
///
/// It is what makes the Apply refuse a stale or foreign preview, the way
/// <c>Inventory.Domain.Costing.CostLedgerFingerprint</c> already does for a costing repair: the
/// Preview reports the fingerprint of the data it projected, and the Apply recomputes it from its own
/// authoritative read inside the same transaction as the write. A purchase edited, added or deleted,
/// a component classified by hand, or a product rule or supplier default changed in between all
/// change it, so the plan the operator approved is no longer the plan that would be applied and the
/// Apply stops instead of writing one nobody saw.
///
/// The owning business id is part of the rendering, which is what binds a preview to its tenant.
/// Without it, two businesses whose purchase histories happen to render identically - two empty
/// histories, most obviously - would accept each other's previews. It is never supplied by a caller:
/// the Application resolves it from the authenticated actor's membership.
///
/// Canonical means a fixed field order, every collection sorted by its own key, and decimals
/// rendered without insignificant trailing zeros so a re-read value of a different scale is not
/// mistaken for a change. The fingerprint is not a secret and does not need to be: it is only ever
/// compared against a value the server recomputes, and a caller that submits anything else is
/// refused. Nothing a caller submits is written, so there is no classification, provenance or total
/// for a tampered request to influence.
/// </summary>
public static class HistoricalGstClassificationFingerprint
{
    /// <summary>
    /// Bumping this invalidates every outstanding preview, which is the intended effect whenever the
    /// rendering below changes: a fingerprint from an older rendering must never be treated as
    /// matching a newer one.
    /// </summary>
    private const string Version = "v1";

    private const char FieldSeparator = '|';
    private const char RecordSeparator = '\n';

    /// <summary>The fingerprint as lowercase hexadecimal SHA-256.</summary>
    public static string Compute(int businessId, IReadOnlyCollection<HistoricalGstPurchase> purchases)
    {
        ArgumentNullException.ThrowIfNull(purchases);

        var canonical = new StringBuilder();
        Append(canonical, Version, Number(businessId), Number(purchases.Count));

        foreach (var purchase in purchases.OrderBy(x => x.PurchaseId))
        {
            var lines = purchase.Lines ?? [];
            Append(
                canonical,
                "p",
                Number(purchase.PurchaseId),
                Money(purchase.DeliveryCost),
                State(purchase.Delivery),
                Money(purchase.PackageCost),
                State(purchase.Package),
                Number((int)purchase.SupplierDefaults.ProductLines),
                Number((int)purchase.SupplierDefaults.Delivery),
                Number((int)purchase.SupplierDefaults.Package),
                Number(lines.Count));

            foreach (var line in lines.OrderBy(x => x.LineId))
            {
                Append(
                    canonical,
                    "l",
                    Number(line.LineId),
                    Number(line.ProductId),
                    Money(line.Quantity),
                    Money(line.UnitCost),
                    State(line.State),
                    Number((int)line.ProductRule));
            }
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static void Append(StringBuilder canonical, params string[] fields) =>
        canonical.AppendJoin(FieldSeparator, fields).Append(RecordSeparator);

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// A classification and its provenance as one field. Rendered as the stored integers rather than
    /// the enum names, so a value outside the declared vocabulary - which this must notice changing
    /// rather than reject - still renders.
    /// </summary>
    private static string State(GstClassificationState state) =>
        $"{(int)state.Classification}.{(int)state.Source}";

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
