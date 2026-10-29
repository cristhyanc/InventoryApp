using System.Text.Json.Serialization;

namespace Inventory.Application.MachineStockSync;

/// <summary>
/// The Dashboard's machine restock-sync reminder (issue #891), which is either the reminder itself
/// (<see cref="MachineRestockSyncReminderDto"/>) or the withheld shape
/// (<see cref="HiddenMachineRestockSyncReminderDto"/>) with the reason this business is not shown
/// one at all.
///
/// <para>A union rather than a reminder with an empty machine list, for the reason the Dashboard's
/// own section union already gives (<c>UnavailableDashboardSectionDto</c>): an empty list reads as
/// "every machine is up to date", and that is a claim about machines - one a business with no Nayax
/// connection, no provider-named machine or a support session has no evidence for. The two
/// <see cref="JsonDerivedTypeAttribute"/> declarations carry no type discriminator, so the JSON is
/// the member's own object and a client branches on the stated <c>applicable</c> flag.</para>
/// </summary>
[JsonDerivedType(typeof(MachineRestockSyncReminderDto))]
[JsonDerivedType(typeof(HiddenMachineRestockSyncReminderDto))]
public interface IMachineRestockSyncReminderDto
{
    /// <summary>Whether this business is shown the reminder at all.</summary>
    bool Applicable { get; }
}

/// <summary>
/// The stable reason codes for a business the reminder does not apply to (issue #891). Codes a
/// client maps to its own wording, never sentences to display.
/// </summary>
public static class MachineRestockSyncReminderHiddenReasons
{
    /// <summary>
    /// The request is a support session's read-only view (issue #551). A session resolves no Nayax
    /// credentials and may take no action, so a reminder to go and sync would name work the reader
    /// cannot do.
    /// </summary>
    public const string SupportView = "SupportView";

    /// <summary>
    /// The business has no Nayax credentials an ordinary operation may use - nothing stored, or a
    /// connection <c>NayaxConnectionStatusGate</c> refuses. Nayax is an optional add-on, so this is
    /// an ordinary property of a working business and not an error.
    /// </summary>
    public const string NayaxNotConnected = "NayaxNotConnected";

    /// <summary>
    /// The business's effective plan does not include the sales provider (issue #967), so there is
    /// no provider restock sync for it to be reminded about. Deliberately distinct from
    /// <see cref="NayaxNotConnected"/>: the credentials may be valid and still saved, and nothing
    /// here changes them.
    /// </summary>
    public const string PlanRequired = "PlanRequired";

    /// <summary>
    /// The business's sales provider names none of its machines, so there is no provider restock
    /// sync for any of them to be overdue for. A machine an operator created themselves is never
    /// counted here.
    /// </summary>
    public const string NoProviderMachines = "NoProviderMachines";
}

/// <summary>
/// What this application can say, from local state alone, about whether the business's Nayax
/// account will actually let a <em>Sync Restock</em> read the machine's alerts (issue #891).
///
/// <para>Deliberately two answers and not three. Whether the Lynx <c>GET
/// /v1/machines/{MachineID}/lastAlerts</c> permission is granted is something only a Nayax call can
/// settle, and this reminder makes none; what is stored is the connection status and whether the
/// last completed check left the machine-scoped checks unverified (issue #537). So the reminder
/// either has a completed check that covered those reads, or it does not - and in the second case it
/// says the sync may be unavailable instead of presenting a working action.</para>
/// </summary>
public static class MachineRestockSyncAvailability
{
    /// <summary>
    /// A completed connection check verified the machine-scoped reads, which include Get Machine
    /// Last Alerts, and the credentials are <c>Ready</c>.
    /// </summary>
    public const string Confirmed = "Confirmed";

    /// <summary>
    /// The credentials are usable enough for an ordinary call to be attempted, but nothing has
    /// confirmed the machine-scoped alerts read: the connection is still
    /// <c>PendingPermissions</c>, or it is <c>Ready</c> with the machine checks unverified because
    /// the business had no machine in Nayax when the check ran.
    /// </summary>
    public const string NotConfirmed = "NotConfirmed";
}

/// <summary>
/// The reminder as it is withheld (issue #891): <c>{ "applicable": false, "reason": "..." }</c>.
/// <see cref="Applicable"/> is a constant <see langword="false"/>, so there is no combination of
/// properties that could describe an applicable reminder inapplicably.
/// </summary>
public sealed record HiddenMachineRestockSyncReminderDto(string Reason) : IMachineRestockSyncReminderDto
{
    /// <summary>Always <see langword="false"/>; see <see cref="IMachineRestockSyncReminderDto"/>.</summary>
    public bool Applicable => false;

    /// <summary>The reminder as a support session is answered.</summary>
    public static HiddenMachineRestockSyncReminderDto SupportView { get; } =
        new(MachineRestockSyncReminderHiddenReasons.SupportView);

    /// <summary>The reminder for a business with no usable Nayax connection.</summary>
    public static HiddenMachineRestockSyncReminderDto NayaxNotConnected { get; } =
        new(MachineRestockSyncReminderHiddenReasons.NayaxNotConnected);

    /// <summary>The reminder for a business whose plan does not include the sales provider.</summary>
    public static HiddenMachineRestockSyncReminderDto PlanRequired { get; } =
        new(MachineRestockSyncReminderHiddenReasons.PlanRequired);

    /// <summary>The reminder for a business whose provider names none of its machines.</summary>
    public static HiddenMachineRestockSyncReminderDto NoProviderMachines { get; } =
        new(MachineRestockSyncReminderHiddenReasons.NoProviderMachines);
}

/// <summary>
/// One machine the reminder lists (issue #891). Every figure is read from this application's own
/// tables; nothing here was fetched from Nayax while the Dashboard loaded.
/// </summary>
/// <param name="LocalMachineId">
/// <c>Machines.Id</c> - the id the machine details route takes, so the row can link to it.
/// </param>
/// <param name="MachineName">What the operator calls the machine.</param>
/// <param name="LastSuccessfulSyncAtUtc">
/// When the machine's last fully successful restock-event fetch <em>started</em>, or
/// <see langword="null"/> for "No recorded sync" - which includes coverage recorded under
/// credentials that have since been replaced. Never an inferred instant.
/// </param>
/// <param name="LastSuccessfulSyncBusinessDate">
/// The business calendar date that instant falls on, which is the date the three-day rule actually
/// compares, or <see langword="null"/> when there is no recorded sync.
/// </param>
/// <param name="DaysSinceLastSuccessfulSync">
/// How many business calendar days ago that was, or <see langword="null"/> when there is no
/// recorded sync. It is not a count of hours and is never negative.
/// </param>
/// <param name="IsFetchOverdue">
/// Whether that is at least <c>MachineRestockSyncReminderPolicy.OverdueAfterDays</c> business
/// calendar days old, or there is no recorded sync at all.
/// </param>
/// <param name="KnownPendingEventCount">
/// How many imported restock events dated inside the pending window are still unresolved -
/// needs-review, insufficient-stock and unresolved possible-duplicate events included; applied and
/// explicitly reconciled ones, and ones reporting a zero or negative quantity, excluded (issue #948).
/// A count of <em>persisted</em> facts: zero does not prove Nayax holds no un-fetched alert, which
/// is why an overdue fetch is reported separately.
/// </param>
/// <param name="OldestKnownPendingEventAtUtc">
/// The canonical event instant of the oldest of those, or <see langword="null"/> when there is
/// none.
/// </param>
/// <param name="OldestKnownPendingEventBusinessDate">
/// The business calendar date that instant falls on, or <see langword="null"/> when there is none
/// (issue #948). The banner's machine link hands it to the Sync Restock dialog as its From date, so
/// the client never dates an instant itself.
/// </param>
/// <param name="HasPendingWorkBeforeDefaultFrom">
/// Whether some of that pending work is dated before the Sync Restock dialog's default From date,
/// so an operator who opens the dialog and leaves the default would not see it.
/// </param>
public sealed record MachineRestockSyncReminderMachineDto(
    long LocalMachineId,
    string MachineName,
    DateTime? LastSuccessfulSyncAtUtc,
    DateTime? LastSuccessfulSyncBusinessDate,
    int? DaysSinceLastSuccessfulSync,
    bool IsFetchOverdue,
    int KnownPendingEventCount,
    DateTime? OldestKnownPendingEventAtUtc,
    DateTime? OldestKnownPendingEventBusinessDate,
    bool HasPendingWorkBeforeDefaultFrom);

/// <summary>
/// The reminder itself (issue #891): the provider-named machines whose explicit <em>Sync
/// Restock</em> needs an operator's attention, with the dates the decision was made against.
///
/// <para>It is a reminder and nothing else. It applies no event, resolves no duplicate, changes no
/// inventory and acknowledges nothing: reading it, following one of its links or opening a preview
/// leaves every pending event exactly as pending as it was.</para>
///
/// <para><see cref="Machines"/> holds only the machines that need attention, so an empty list on an
/// applicable reminder means every provider-named machine was synced inside the window and has no
/// unresolved event - a real answer about machines this read did evaluate.</para>
/// </summary>
/// <param name="AsOfUtc">The single instant every date below was resolved against.</param>
/// <param name="BusinessDate">
/// The business calendar date that instant falls on, in the business's own time zone (issue #499) -
/// the "today" the three-day rule compares against.
/// </param>
/// <param name="OverdueAfterDays">
/// The threshold in business calendar days, published so a client never restates it.
/// </param>
/// <param name="DefaultFromBusinessDate">
/// The From date the Sync Restock dialog will default to, so the instruction to clear or move it
/// back can name the date an operator will actually see.
/// </param>
/// <param name="PendingLookbackDays">
/// How many business calendar days back pending events are counted (issue #948), published so a
/// client never restates it.
/// </param>
/// <param name="PendingFromBusinessDate">
/// The first business calendar date whose pending events are counted. Older unresolved events are
/// still stored and still listed by Sync Restock; they are only not counted here.
/// </param>
/// <param name="SyncAvailability">
/// Whether a completed connection check confirmed the machine alerts read; see
/// <see cref="MachineRestockSyncAvailability"/>.
/// </param>
/// <param name="Machines">The machines needing attention, ordered by name.</param>
public sealed record MachineRestockSyncReminderDto(
    DateTime AsOfUtc,
    DateTime BusinessDate,
    int OverdueAfterDays,
    DateTime DefaultFromBusinessDate,
    int PendingLookbackDays,
    DateTime PendingFromBusinessDate,
    string SyncAvailability,
    IReadOnlyList<MachineRestockSyncReminderMachineDto> Machines) : IMachineRestockSyncReminderDto
{
    /// <summary>Always <see langword="true"/>; see <see cref="IMachineRestockSyncReminderDto"/>.</summary>
    public bool Applicable => true;
}
