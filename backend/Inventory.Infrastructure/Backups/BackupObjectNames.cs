using System.Globalization;

namespace Inventory.Infrastructure.Backups;

/// <summary>
/// The object names a verified snapshot is stored under (issue #332).
///
/// Two prefixes, with deliberately different naming rules:
///
/// <list type="bullet">
/// <item><description>
/// <c>daily/</c> is one object per snapshot, named from the snapshot's UTC instant to the second.
/// It is unique per snapshot and never derived from anything a caller supplies, so two snapshots
/// cannot collide unless they were taken in the same second - and if they were, the conditional
/// create refuses the second rather than overwriting the first.
/// </description></item>
/// <item><description>
/// <c>monthly/YYYY-MM/</c> is one object per calendar month, named deterministically from the
/// month alone. That determinism is the whole mechanism: every upload in a month computes the
/// same name, so "is this the first upload of the month?" is answered by whether a conditional
/// create of that name succeeds, with no listing, no read-then-write, and no way for a later
/// upload to replace an earlier month's recovery point.
/// </description></item>
/// </list>
///
/// Both are formed from UTC and the invariant culture, so the host's timezone and locale cannot
/// change where a snapshot lands. The business reporting timezone is irrelevant here: these are
/// operational recovery points, not reporting periods.
/// </summary>
public static class BackupObjectNames
{
    /// <summary>The prefix every per-snapshot object sits under.</summary>
    public const string DailyPrefix = "daily/";

    /// <summary>The prefix every per-month recovery point sits under.</summary>
    public const string MonthlyPrefix = "monthly/";

    /// <summary>The object name for one snapshot, from its UTC creation instant.</summary>
    public static string Daily(DateTimeOffset createdUtc) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{DailyPrefix}inventory-{createdUtc.ToUniversalTime():yyyyMMdd'T'HHmmss}Z.db");

    /// <summary>The deterministic object name for the UTC calendar month's recovery point.</summary>
    public static string Monthly(DateTimeOffset createdUtc)
    {
        var month = createdUtc.ToUniversalTime().ToString("yyyy-MM", CultureInfo.InvariantCulture);

        return string.Create(CultureInfo.InvariantCulture, $"{MonthlyPrefix}{month}/inventory-{month}.db");
    }
}

/// <summary>
/// The metadata recorded on every uploaded snapshot (issue #332).
///
/// All three are operational facts about the object, safe to read from a storage browser: when the
/// snapshot was taken, the checksum its integrity verification produced, and which prefix it was
/// written for. Nothing here describes, summarises or samples the data inside the snapshot, and
/// nothing here is or could become a credential.
///
/// Azure Blob metadata names must be valid C# identifiers and are returned lower-case, so they are
/// written lower-case and read case-insensitively.
/// </summary>
public static class BackupObjectMetadata
{
    /// <summary>When the snapshot was taken, as <c>yyyy-MM-ddTHH:mm:ssZ</c>.</summary>
    public const string CreatedUtc = "createdutc";

    /// <summary>The snapshot's SHA-256, lower-case hex, as verification computed it.</summary>
    public const string Sha256 = "sha256";

    /// <summary><c>daily</c> or <c>monthly</c>: which recovery point this object is.</summary>
    public const string Kind = "kind";
}
