using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Inventory.Domain.Stock;

/// <summary>
/// A deterministic fingerprint of what one batch refill request asks for (issue #743): the machine
/// it names, its note, and every row it carries.
///
/// <para>It is what makes an idempotency key mean something. A key is a promise that the same
/// submission may be sent again safely, so the server has to be able to tell "this is the retry I
/// already recorded" from "this is a different pick under a key that is already spent". The first is
/// answered with the original result and records nothing new; the second is refused with
/// <see cref="PickListRefillPolicy.IdempotencyKeyReusedMessage"/>, because recording it would be
/// exactly the double deduction the key exists to prevent.</para>
///
/// <para><strong>Canonical means the same request renders identically however it was typed.</strong>
/// Rows are ordered by product id, so the same pick entered in another order is the same pick - a
/// product may appear only once per request, so that ordering is total. The note and the machine are
/// rendered as given, already normalised by the caller (a blank note is <see langword="null"/>), and
/// a null machine and a null note each render as a value no id and no text can collide with.</para>
///
/// <para>The owning business is deliberately <em>not</em> part of the rendering, unlike
/// <c>Inventory.Domain.Gst.HistoricalGstClassificationFingerprint</c>. That fingerprint travels to a
/// client and back, so it has to be bound to its tenant. This one never leaves the server: it is
/// stored beside the key on the business's own row and only ever compared with a value recomputed
/// for a request already scoped to that business, so the business id would bind nothing that the
/// per-business uniqueness of the key does not already bind.</para>
/// </summary>
public static class PickListRefillFingerprint
{
    /// <summary>
    /// Bumping this invalidates every stored fingerprint, which is the intended effect whenever the
    /// rendering below changes: a value from an older rendering must never be treated as matching a
    /// newer one. A key whose stored fingerprint no longer matches is refused as a reused key, which
    /// is the fail-closed direction - a retry is refused rather than recorded twice.
    /// </summary>
    private const string Version = "v1";

    private const char FieldSeparator = '|';
    private const char RecordSeparator = '\n';

    /// <summary>The fingerprint as lowercase hexadecimal SHA-256.</summary>
    /// <param name="machineId">The local machine the pick was for, or <see langword="null"/>.</param>
    /// <param name="notes">The note, already normalised to <see langword="null"/> when blank.</param>
    /// <param name="rows">Every submitted row; rendered in product-id order rather than as given.</param>
    public static string Compute(
        long? machineId, string? notes, IReadOnlyCollection<PickListRefillRequestRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var canonical = new StringBuilder();
        Append(canonical, Version, Machine(machineId), Notes(notes), Number(rows.Count));

        foreach (var row in rows.OrderBy(row => row.ProductId))
        {
            Append(canonical, "r", Number(row.ProductId), Number(row.QuantityTaken));
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static void Append(StringBuilder canonical, params string[] fields) =>
        canonical.AppendJoin(FieldSeparator, fields).Append(RecordSeparator);

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// A machine id, or a word no id renders as - so "no machine" and a machine called nothing
    /// cannot be the same rendering.
    /// </summary>
    private static string Machine(long? machineId) =>
        machineId is { } id ? Number(id) : "no-machine";

    /// <summary>
    /// The note's length and its text, so a note cannot borrow a field separator and make two
    /// different requests render the same way.
    /// </summary>
    private static string Notes(string? notes) =>
        notes is null ? "no-notes" : $"{Number(notes.Length)}:{notes}";
}
