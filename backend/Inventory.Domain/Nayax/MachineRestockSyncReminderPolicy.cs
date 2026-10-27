namespace Inventory.Domain.Nayax;

/// <summary>
/// The deterministic rules behind the Dashboard's machine restock-sync reminder (issue #891): when
/// a machine's explicit <em>Sync Restock</em> fetch is overdue, when its known pending restock
/// events still need an operator, and whether recorded coverage describes the credentials in use
/// now.
///
/// <para>It lives in the Domain, beside <see cref="NayaxConnectionStatusGate"/> and the Sync
/// Restock match/impact policies, because the three-day rule is the meaning of "overdue" rather
/// than a presentation choice: the reminder endpoint, its tests and the banner's wording all read
/// this one table, and the Sync Restock dialog's own From-date default is stated here too so the
/// reminder cannot name a different date from the one the dialog will actually apply.</para>
///
/// <para><strong>Three business calendar days, not 72 hours.</strong> Every comparison here is
/// between business calendar <em>dates</em> - the current business's own, derived by
/// <c>Inventory.Application.Time.IBusinessCalendar</c> - so a daylight-saving transition that makes
/// a day 23 or 25 hours long cannot move the boundary, and an operator who syncs each morning is
/// never warned because the clocks changed. The times of day on the arguments are deliberately
/// discarded.</para>
///
/// <para><strong>Nothing here infers a successful sync.</strong> A recorded fetch instant is the
/// only evidence, and the absence of one is reported as the absence of one: it is never derived
/// from an event's <c>CreatedAt</c> or <c>ProcessedAt</c>, from a latest-sales or catalogue sync, or
/// from a machine stock count. It also never claims a machine has <em>never</em> synced - machines
/// restocked before this metadata existed have no record of syncs that really happened - which is
/// why the published state is "no recorded sync" and not "never synced".</para>
///
/// <para>It holds no clock, no database and no configuration: every answer is a function of its
/// arguments.</para>
/// </summary>
public static class MachineRestockSyncReminderPolicy
{
    /// <summary>
    /// How many business calendar days without a fully successful restock-event fetch make a
    /// machine overdue. Three, which is also the Sync Restock dialog's From-date default window
    /// (issue #206, shortened from seven by #242), so the reminder and the dialog describe one
    /// window rather than two.
    /// </summary>
    public const int OverdueAfterDays = 3;

    /// <summary>
    /// Whether the machine's last fully successful restock-event fetch is at least
    /// <see cref="OverdueAfterDays"/> business calendar days old.
    ///
    /// <paramref name="lastSuccessfulSyncBusinessDate"/> is <see langword="null"/> when nothing
    /// records a successful fetch for the credentials in use, which is overdue: a machine nobody
    /// can show a sync for must be visible, never quietly healthy.
    ///
    /// A date after <paramref name="businessToday"/> - clock skew, or a business whose time zone
    /// was corrected - is not overdue, and is not counted as negative age either; see
    /// <see cref="DaysSinceLastSuccessfulSync"/>.
    /// </summary>
    public static bool IsFetchOverdue(DateTime? lastSuccessfulSyncBusinessDate, DateTime businessToday) =>
        DaysSinceLastSuccessfulSync(lastSuccessfulSyncBusinessDate, businessToday) is not { } days
        || days >= OverdueAfterDays;

    /// <summary>
    /// How many business calendar days ago the last fully successful fetch was, or
    /// <see langword="null"/> when none is recorded for the credentials in use.
    ///
    /// Clamped at zero, because a recorded date in the future is a skew rather than a negative age,
    /// and "synced -1 days ago" is not something to put on a banner.
    /// </summary>
    public static int? DaysSinceLastSuccessfulSync(
        DateTime? lastSuccessfulSyncBusinessDate, DateTime businessToday) =>
        lastSuccessfulSyncBusinessDate is { } lastSynced
            ? Math.Max(0, (businessToday.Date - lastSynced.Date).Days)
            : null;

    /// <summary>
    /// The From date the Sync Restock dialog defaults to for this business date: three calendar
    /// days before it. Stated here so the reminder's instruction to clear or move the From date back
    /// names the date the dialog will really start from.
    /// </summary>
    public static DateTime DefaultFromBusinessDate(DateTime businessToday) =>
        businessToday.Date.AddDays(-OverdueAfterDays);

    /// <summary>
    /// Whether the machine has known pending restock work dated before the Sync Restock dialog's
    /// default From date, so opening the dialog with that default would hide it.
    ///
    /// <paramref name="oldestKnownPendingEventBusinessDate"/> is <see langword="null"/> when no
    /// pending event is known, which hides nothing.
    /// </summary>
    public static bool HasPendingWorkBeforeDefaultFrom(
        DateTime? oldestKnownPendingEventBusinessDate, DateTime businessToday) =>
        oldestKnownPendingEventBusinessDate is { } oldest
        && oldest.Date < DefaultFromBusinessDate(businessToday);

    /// <summary>
    /// Whether this machine belongs on the reminder: either its fetch is overdue, or it has known
    /// pending restock events.
    ///
    /// The pending half deliberately carries no date bound. An event that ages out of the
    /// three-day window is still unresolved work, and letting the window clear it is exactly the
    /// silent disappearance this reminder exists to prevent.
    /// </summary>
    public static bool NeedsAttention(bool isFetchOverdue, int knownPendingEventCount) =>
        isFetchOverdue || knownPendingEventCount > 0;

    /// <summary>
    /// Whether recorded coverage describes the credentials the business would sync with now.
    ///
    /// Coverage is stamped with the Nayax credential revision the fetch authenticated under
    /// (AGENTS.md § Nayax integration), and a save supersedes everything the previous token proved.
    /// So only an exact match is current: an older revision's success says nothing about the token
    /// that replaced it, and a revision <em>newer</em> than the stored connection's cannot be
    /// trusted either - it fails closed rather than being read as unusually fresh.
    /// </summary>
    public static bool IsCoverageCurrent(int? recordedCredentialRevision, int currentCredentialRevision) =>
        recordedCredentialRevision == currentCredentialRevision;
}
