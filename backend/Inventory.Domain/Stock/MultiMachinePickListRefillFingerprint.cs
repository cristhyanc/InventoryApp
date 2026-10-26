using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Inventory.Domain.Stock;

/// <summary>
/// A deterministic fingerprint of what one multi-machine refill batch asks for (issue #897): its note
/// and every allocation it carries.
///
/// <para>It is what makes an idempotency key mean something. A key is a promise that the same
/// submission may be sent again safely, so the server has to be able to tell "this is the retry I
/// already recorded" from "this is a different pick under a key that is already spent". The first is
/// answered with the original result and records nothing new; the second is refused with
/// <see cref="PickListRefillPolicy.IdempotencyKeyReusedMessage"/>, because recording it would be
/// exactly the double deduction the key exists to prevent.</para>
///
/// <para><strong>Canonical means the same batch renders identically however it was typed.</strong>
/// Allocations are ordered by machine and then by product, so the same run entered machine by machine
/// or product by product is the same run - a machine/product pair may appear only once per request, so
/// that ordering is total. The note is rendered as given, already normalised by the caller (a blank
/// note is <see langword="null"/>), and a null note renders as a value no text can collide with.</para>
///
/// <para><strong>Its version tag is what keeps the two refill contracts apart.</strong> The key column
/// is one per-business namespace shared with the single-machine batch
/// (<see cref="PickListRefillFingerprint"/>, version <c>v1</c>), so a key that recorded one kind must
/// never replay as the other. Because the rendering starts with a different version, the two can never
/// produce the same fingerprint, and a key already spent on a single-machine refill is therefore
/// refused as a reused key rather than answered with a batch it did not record - and the other way
/// round. Nothing about an already-recorded refill is reinterpreted.</para>
///
/// <para>The owning business is deliberately <em>not</em> part of the rendering, for the same reason
/// it is not part of <see cref="PickListRefillFingerprint"/>'s: this value never leaves the server and
/// is only ever compared with one recomputed for a request already scoped to that business.</para>
/// </summary>
public static class MultiMachinePickListRefillFingerprint
{
    /// <summary>
    /// Bumping this invalidates every stored fingerprint of this contract, which is the intended
    /// effect whenever the rendering below changes: a value from an older rendering must never be
    /// treated as matching a newer one. A key whose stored fingerprint no longer matches is refused as
    /// a reused key, which is the fail-closed direction - a retry is refused rather than recorded
    /// twice.
    /// </summary>
    private const string Version = "mm1";

    private const char FieldSeparator = '|';
    private const char RecordSeparator = '\n';

    /// <summary>The fingerprint as lowercase hexadecimal SHA-256.</summary>
    /// <param name="notes">The note, already normalised to <see langword="null"/> when blank.</param>
    /// <param name="lines">
    /// Every validated allocation; rendered in machine-then-product order rather than as given.
    /// </param>
    public static string Compute(string? notes, IReadOnlyCollection<MultiMachineRefillLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var canonical = new StringBuilder();
        Append(canonical, Version, Notes(notes), Number(lines.Count));

        foreach (var line in lines.OrderBy(line => line.MachineId).ThenBy(line => line.ProductId))
        {
            Append(canonical, "a", Number(line.MachineId), Number(line.ProductId), Number(line.QuantityTaken));
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static void Append(StringBuilder canonical, params string[] fields) =>
        canonical.AppendJoin(FieldSeparator, fields).Append(RecordSeparator);

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The note's length and its text, so a note cannot borrow a field separator and make two
    /// different requests render the same way.
    /// </summary>
    private static string Notes(string? notes) =>
        notes is null ? "no-notes" : $"{Number(notes.Length)}:{notes}";
}
