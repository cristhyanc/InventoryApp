using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Inventory.Domain.Gst;

namespace Inventory.Domain.Purchases;

/// <summary>
/// One line of a purchase create request, as <see cref="PurchaseCreateFingerprint"/> renders it:
/// the product, the quantity, the unit cost and the GST classification the caller submitted (or
/// none, which is a different request from an explicit <c>Unknown</c>).
/// </summary>
public sealed record PurchaseFingerprintLine(
    long ProductId, decimal Quantity, decimal UnitCost, GstClassification? GstClassification);

/// <summary>
/// Everything one purchase create request asks for, as submitted. It is deliberately the
/// <em>submitted</em> request rather than the resolved one: a default this server fills in - the
/// purchase date taken from the clock when none was sent, the title taken from the uploaded file's
/// name - would make two identical retries render differently and turn the second one into a
/// conflict.
/// </summary>
public sealed record PurchaseFingerprintContent(
    string? Title,
    string? Notes,
    string? InvoiceNumber,
    decimal? TotalAmount,
    decimal? DeliveryCost,
    GstClassification? DeliveryGstClassification,
    decimal? PackageCost,
    GstClassification? PackageGstClassification,
    DateTime? PurchaseDate,
    int? SupplierId,
    string? DocumentFileName,
    long? DocumentLength,
    int? CompleteSupplierOrderId,
    string? CompleteSupplierOrderFingerprint,
    IReadOnlyList<PurchaseFingerprintLine> Lines);

/// <summary>
/// A deterministic fingerprint of what one purchase create request asks for (issue #1247), the
/// purchase's counterpart of <c>Inventory.Domain.Stock.PickListRefillFingerprint</c>.
///
/// <para>It is what makes an idempotency key mean something. A key is a promise that the same
/// submission may be sent again safely, so the server has to be able to tell "this is the retry I
/// already saved" from "this is a different purchase under a key that is already spent". The first
/// is answered with the purchase that was saved and writes nothing; the second is refused with
/// <see cref="PurchaseIdempotencyPolicy.KeyReusedMessage"/>, because saving it would be exactly the
/// doubled stock and inventory value the key exists to prevent.</para>
///
/// <para><strong>Lines are rendered in the order they were submitted, not sorted.</strong> Unlike a
/// pick, a purchase may legitimately carry two lines for one product - two pack sizes of the same
/// drink on one receipt - so there is no total ordering by product to sort into, and reordering the
/// lines of a saved purchase is a different submission. A retry resends the request byte for byte,
/// which is the case this has to answer; a human who retypes the same receipt in another order gets
/// a new key from the form anyway.</para>
///
/// <para><strong>The document is rendered by its name and its length, never by its bytes.</strong>
/// Hashing an upload would mean reading it twice, and the two values the request already carries are
/// enough to tell "the same receipt again" from "a different receipt under a spent key". A purchase
/// with no document renders as a value no file name can collide with.</para>
///
/// <para>The owning business is deliberately <em>not</em> rendered, for
/// <c>PickListRefillFingerprint</c>'s reason: this value never leaves the server, is stored beside
/// the key on the business's own row and is only ever compared with a value recomputed for a request
/// already scoped to that business, so the business id would bind nothing the per-business
/// uniqueness of the key does not already bind.</para>
/// </summary>
public static class PurchaseCreateFingerprint
{
    /// <summary>
    /// Bumping this invalidates every stored fingerprint, which is the intended effect whenever the
    /// rendering below changes: a value from an older rendering must never be treated as matching a
    /// newer one. A key whose stored fingerprint no longer matches is refused as a reused key, which
    /// is the fail-closed direction - a retry is refused rather than saved twice.
    /// </summary>
    private const string Version = "v1";

    private const char FieldSeparator = '|';
    private const char RecordSeparator = '\n';

    /// <summary>The fingerprint as lowercase hexadecimal SHA-256, which is 64 characters.</summary>
    public static string Compute(PurchaseFingerprintContent content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var canonical = new StringBuilder();
        Append(
            canonical,
            Version,
            Text("title", content.Title),
            Text("notes", content.Notes),
            Text("invoice", content.InvoiceNumber));
        Append(
            canonical,
            Money(content.TotalAmount),
            Money(content.DeliveryCost),
            Classification(content.DeliveryGstClassification),
            Money(content.PackageCost),
            Classification(content.PackageGstClassification));
        Append(
            canonical,
            Instant(content.PurchaseDate),
            Number(content.SupplierId),
            Text("file", content.DocumentFileName),
            Number(content.DocumentLength),
            Number(content.CompleteSupplierOrderId),
            Text("order", content.CompleteSupplierOrderFingerprint),
            Count(content.Lines.Count));

        foreach (var line in content.Lines)
        {
            Append(
                canonical,
                "l",
                Number(line.ProductId),
                Money(line.Quantity),
                Money(line.UnitCost),
                Classification(line.GstClassification));
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static void Append(StringBuilder canonical, params string[] fields) =>
        canonical.AppendJoin(FieldSeparator, fields).Append(RecordSeparator);

    /// <summary>
    /// A text value rendered with its own length and a label, so no value can borrow a field
    /// separator and make two different requests render the same way, and absent text cannot
    /// collide with text somebody typed.
    /// </summary>
    private static string Text(string label, string? value) =>
        value is null ? $"no-{label}" : $"{label}:{value.Length}:{value}";

    /// <summary>
    /// An amount rendered invariantly, including the scale it was submitted with: a retry resends
    /// the same request, so it parses to the same <see cref="decimal"/> and renders identically.
    /// </summary>
    private static string Money(decimal? value) =>
        value is { } amount ? amount.ToString(CultureInfo.InvariantCulture) : "no-amount";

    private static string Number(long? value) =>
        value is { } number ? number.ToString(CultureInfo.InvariantCulture) : "no-number";

    /// <summary>How many lines the request carries, so a truncated submission cannot render as a prefix of a longer one.</summary>
    private static string Count(int lines) => lines.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The instant round-tripped, so the kind is part of the rendering too: a request that names a
    /// local time and one that names the same wall clock in UTC are different submissions.
    /// </summary>
    private static string Instant(DateTime? value) =>
        value is { } instant ? instant.ToString("O", CultureInfo.InvariantCulture) : "no-date";

    /// <summary>
    /// A submitted classification, or a value no classification renders as - "not submitted" and
    /// "submitted as Unknown" are different requests on this contract (issue #429).
    /// </summary>
    private static string Classification(GstClassification? value) =>
        value is { } classification
            ? ((int)classification).ToString(CultureInfo.InvariantCulture)
            : "no-classification";
}
