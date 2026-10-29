namespace Inventory.Domain.Attachments;

/// <summary>
/// How close a business is to its attachment storage allowance (issue #968), as
/// <c>GET /api/me/access</c> reports it. Three states, decided once here so a page, an export and
/// an upload refusal cannot disagree about when a business is "nearly full".
/// </summary>
public enum AttachmentStorageWarningLevel
{
    /// <summary>Under 80% of the allowance: nothing to say.</summary>
    Normal = 10,

    /// <summary>At or over 80% and still under the allowance: the business should be warned.</summary>
    Warning = 20,

    /// <summary>
    /// At or over the allowance. New attachments are refused; everything else - viewing,
    /// downloading, deleting, and recording purchases and expenses without a document - keeps
    /// working, and nothing is ever deleted automatically.
    /// </summary>
    Full = 30,
}

/// <summary>
/// The one attachment storage quota rule (issue #968, child of #965, decided 29 October 2026).
///
/// <para><strong>What usage is.</strong> The stored bytes of the attachments referenced by the
/// business's active purchase and expense rows, plus its unexpired upload reservations. A physical
/// file no row refers to is an orphan and is deliberately <em>not</em> charged to the business: it
/// is reported by the reconciliation command instead, because charging a business for bytes it
/// cannot see or delete would be a bill it has no way to settle. The limit itself is the plan's -
/// <c>Inventory.Domain.Subscriptions.PlanCatalogue</c>'s <c>AttachmentStorageLimitBytes</c> - and is
/// never restated here.</para>
///
/// <para><strong>What an upload costs.</strong> A new attachment costs its whole size; a
/// replacement costs only its net increase, <c>max(0, new − old)</c>, because the old file's bytes
/// stop counting in the same transaction that points the row at the new one. The same figure is
/// used everywhere a quota decision is made - when the upload is reserved, and again if the
/// reservation has expired by the time the row commits - so a business can never be charged twice
/// for one upload or credited twice for one old file.</para>
///
/// <para><strong>The limit is inclusive.</strong> An upload that lands exactly on the allowance
/// succeeds; the next byte does not. A business that is <em>over</em> its allowance - which a
/// downgrade produces, and which this application never resolves by deleting anything - still
/// passes an upload that costs nothing, which is what keeps a same-size or smaller replacement
/// working at full storage.</para>
///
/// <para><strong>Reservations expire.</strong> A reservation is taken before any bytes are written
/// and stops counting <see cref="ReservationLifetime"/> after it was taken, so a crashed or
/// abandoned upload releases the business's storage on its own rather than holding it until
/// somebody notices. An expired reservation is never committed on trust: the commit re-validates
/// the quota inside its own transaction.</para>
///
/// <para>It is deterministic and total: no clock of its own, no database, no configuration. Every
/// instant it is asked about is supplied by the caller.</para>
/// </summary>
public static class AttachmentStorageQuotaPolicy
{
    /// <summary>
    /// How long an upload reservation counts against the business. Thirty minutes is long enough
    /// for the slowest allowed upload (10 MiB) on a poor connection and short enough that an
    /// abandoned one does not strand a meaningful share of a 1 GB allowance for a working day.
    /// </summary>
    public static readonly TimeSpan ReservationLifetime = TimeSpan.FromMinutes(30);

    /// <summary>
    /// The percentage of the allowance at which a business is warned, as an exact fraction. It is
    /// applied by cross-multiplication rather than by computing a percentage, so the state turns on
    /// the exact byte at every plan limit instead of on a rounded one.
    /// </summary>
    private const long WarningNumerator = 4;
    private const long WarningDenominator = 5;

    /// <summary>
    /// What an upload is refused with when it does not fit. It names no figure: the current usage,
    /// the allowance and the warning state are reported by <c>GET /api/me/access</c>, which is one
    /// place rather than a number copied into an error message.
    /// </summary>
    public const string StorageFullMessage =
        "This upload would exceed the business's attachment storage allowance. "
        + "Delete attachments you no longer need, or upgrade the plan, and try again. "
        + "The record itself can still be saved without a document.";

    /// <summary>
    /// What the loser of two concurrent replacements of one attachment is told. Exactly one of them
    /// commits; the other's row is left as it was, its reservation is released and its file is
    /// removed, so retrying is safe.
    /// </summary>
    public const string ReplacementConflictMessage =
        "This attachment was replaced by someone else while the upload was in progress. "
        + "Reload the record and try again.";

    /// <summary>
    /// What this upload is charged against the allowance.
    /// </summary>
    /// <param name="uploadBytes">The uploaded file's size.</param>
    /// <param name="currentAttachmentBytes">
    /// The size of the attachment being replaced, or <see langword="null"/> when the record has
    /// none - which is also what an attachment of unknown size has to be treated as, because
    /// crediting bytes nobody has measured would be guessing.
    /// </param>
    public static long ChargeableBytes(long uploadBytes, long? currentAttachmentBytes)
    {
        if (currentAttachmentBytes is not { } current) return uploadBytes;

        var increase = uploadBytes - current;
        return increase > 0 ? increase : 0;
    }

    /// <summary>
    /// Whether an upload charged <paramref name="chargeableBytes"/> fits, given everything the
    /// business already uses. Exact-limit succeeds.
    /// </summary>
    public static bool Fits(long usedBytes, long chargeableBytes, long limitBytes) =>
        // An upload that costs nothing always fits, whatever the business already uses. That is
        // not a shortcut: a business over its allowance after a downgrade must still be able to
        // replace a receipt with a same-size or smaller one, and refusing a charge of zero would
        // withhold something that takes no storage at all.
        chargeableBytes <= 0 || usedBytes + chargeableBytes <= limitBytes;

    /// <summary>Which warning state <paramref name="usedBytes"/> puts the business in.</summary>
    public static AttachmentStorageWarningLevel WarningLevelFor(long usedBytes, long limitBytes)
    {
        if (usedBytes >= limitBytes) return AttachmentStorageWarningLevel.Full;

        return usedBytes * WarningDenominator >= limitBytes * WarningNumerator
            ? AttachmentStorageWarningLevel.Warning
            : AttachmentStorageWarningLevel.Normal;
    }

    /// <summary>When a reservation taken at <paramref name="takenAtUtc"/> stops counting.</summary>
    public static DateTime ReservationExpiryFor(DateTime takenAtUtc) => takenAtUtc + ReservationLifetime;

    /// <summary>
    /// Whether a reservation expiring at <paramref name="expiresAtUtc"/> has expired by
    /// <paramref name="nowUtc"/>. The expiry instant itself is expired - the half-open convention
    /// the entitlement periods already use.
    /// </summary>
    public static bool HasExpired(DateTime expiresAtUtc, DateTime nowUtc) => nowUtc >= expiresAtUtc;
}
