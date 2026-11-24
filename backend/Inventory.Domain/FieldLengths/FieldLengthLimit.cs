using System.Globalization;

namespace Inventory.Domain.FieldLengths;

/// <summary>
/// The shared arithmetic behind each record's field length policy (issue #1286, audit #1226 finding
/// DB-03): how a free-text value is measured against its limit, the sentence a refused value is
/// answered with, and when an update counts as leaving a stored value alone.
///
/// <para><strong>A value is measured after trimming.</strong> Surrounding whitespace is not what a
/// person meant to store, so a value that is within the limit once trimmed is never refused because
/// of a trailing space. Nothing here changes what is stored: each record keeps its own existing
/// normalisation, and this only decides whether the value may be written at all.</para>
///
/// <para><strong>A value already stored is never re-judged.</strong> Rows written before the limits
/// existed may hold a longer value, and the limits change no stored data. An update that submits the
/// stored value again - the edit form sends every field back, changed or not - therefore leaves that
/// field alone and is not refused for it; only a new over-long value is.</para>
///
/// <para>Deterministic and free of persistence: the use case, the adapter that compares against the
/// stored row inside its write, and a test all decide identically.</para>
/// </summary>
public static class FieldLengthLimit
{
    /// <summary>The length a value is judged on: its trimmed length, and <c>0</c> for none.</summary>
    public static int MeasuredLength(string? value) => value?.Trim().Length ?? 0;

    /// <summary>
    /// The sentence a value longer than <paramref name="maxLength"/> is refused with. It names the
    /// field and the limit, so the person knows which input to shorten and by how much.
    /// </summary>
    public static string TooLongMessage(string fieldLabel, int maxLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldLabel);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{fieldLabel} must be {maxLength:N0} characters or fewer.");
    }

    /// <summary>
    /// The refusal for a newly submitted value, or <c>null</c> when it is within the limit. A value
    /// exactly at the limit is accepted; nothing at all (<c>null</c> or blank) is never too long.
    /// </summary>
    public static string? Refusal(string fieldLabel, string? submitted, int maxLength) =>
        MeasuredLength(submitted) > maxLength ? TooLongMessage(fieldLabel, maxLength) : null;

    /// <summary>
    /// The refusal for a value submitted on an update, judged against what is stored: <c>null</c>
    /// when the submission leaves the stored value as it is (see <see cref="IsUnchanged"/>), and
    /// otherwise the same answer <see cref="Refusal"/> gives.
    /// </summary>
    public static string? ChangeRefusal(string fieldLabel, string? stored, string? submitted, int maxLength) =>
        IsUnchanged(stored, submitted) ? null : Refusal(fieldLabel, submitted, maxLength);

    /// <summary>
    /// Whether <paramref name="submitted"/> is the value already stored, compared on the same
    /// trimmed text the length is measured on, and with blank and none as one state.
    /// </summary>
    public static bool IsUnchanged(string? stored, string? submitted) =>
        string.Equals(stored?.Trim() ?? string.Empty, submitted?.Trim() ?? string.Empty, StringComparison.Ordinal);
}
