using Inventory.Application.Nayax;
using Inventory.Application.Subscriptions;
using Inventory.Application.Tenancy;
using Inventory.Application.Time;
using Inventory.Domain.Nayax;
using Inventory.Domain.Subscriptions;

namespace Inventory.Application.MachineStockSync;

/// <summary>
/// The Dashboard's machine restock-sync reminder use case (issue #891): which provider-named
/// machines have not had a fully successful <em>Sync Restock</em> fetch in the last three business
/// calendar days, and which still hold unresolved imported restock events.
///
/// <para><strong>It is about the explicit Sync Restock workflow only.</strong> Not the automatic
/// latest-sales refresh, not the catalogue sync and not a machine stock count: the only evidence it
/// reads is the coverage that <see cref="SyncMachineStockFromNayax"/> itself records on full
/// success, and the unresolved state of the events that workflow imported. Nothing is inferred from
/// an event's <c>CreatedAt</c> or <c>ProcessedAt</c>, from a sales or catalogue sync, or from a
/// count.</para>
///
/// <para><strong>It makes no Nayax request and decrypts no credential.</strong> Two local reads: the
/// business's own connection record without its token (<see cref="INayaxConnectionStore.FindAsync"/>
/// - the status and the credential revision, never the ciphertext), and one aggregate query over the
/// machines and their imported events. That is what makes it safe on every Dashboard load, and it is
/// why the reminder is a separate endpoint from the summary: the live fleet read behind the refill
/// card has nothing to do with it, and a failure of either must not take the other down.</para>
///
/// <para><strong>Three business calendar days, decided once.</strong> The instant and the business
/// date are resolved before anything is read, so every machine on the banner is judged against the
/// same "today", and the comparison is a date difference in the current business's own time zone
/// (issue #499) rather than 72 elapsed hours - see
/// <see cref="MachineRestockSyncReminderPolicy"/>, which owns every rule here.</para>
///
/// <para><strong>Four businesses are shown nothing at all</strong>, each with its own stable
/// reason: a support session (issue #551 resolves no Nayax credentials, and a reader may take no
/// action), a business whose plan does not include the sales provider (issue #967 - the sync itself
/// is refused by the credential gate, so reminding anybody to run it would name work the application
/// will not do), a business with no credentials the status gate allows, and a business whose provider
/// names none of its machines. None of those is an empty reminder, because an empty reminder would
/// read as "every machine is up to date". The plan is asked about before the connection is read, for
/// the reason the credential gate asks in that order: nothing about a business's credentials decides
/// this answer, and nothing here changes them.</para>
///
/// <para><strong>It acknowledges nothing and writes nothing.</strong> Reading it leaves every
/// pending event pending, applies no restock, resolves no duplicate and changes no inventory
/// quantity, cost or connection status.</para>
/// </summary>
public sealed class GetMachineRestockSyncReminder
{
    private readonly IClock _clock;
    private readonly IBusinessCalendar _businessCalendar;
    private readonly IBusinessScope _businessScope;
    private readonly CurrentPlan _plan;
    private readonly INayaxConnectionStore _connections;
    private readonly IMachineRestockSyncCoverageStore _coverage;

    public GetMachineRestockSyncReminder(
        IClock clock,
        IBusinessCalendar businessCalendar,
        IBusinessScope businessScope,
        CurrentPlan plan,
        INayaxConnectionStore connections,
        IMachineRestockSyncCoverageStore coverage)
    {
        _clock = clock;
        _businessCalendar = businessCalendar;
        _businessScope = businessScope;
        _plan = plan;
        _connections = connections;
        _coverage = coverage;
    }

    /// <param name="cancellationToken">The request's cancellation token.</param>
    public async Task<IMachineRestockSyncReminderDto> Handle(CancellationToken cancellationToken)
    {
        // Decided from the scope state the credential provider itself refuses in (issue #551), so
        // "the reminder is hidden" and "Nayax is blocked" cannot come to mean different things. It
        // is checked before anything is read: a support reader is told the reminder does not apply
        // to their view, not shown restock work they are not allowed to do.
        if (_businessScope.State == BusinessScopeState.SupportReadOnly)
        {
            return HiddenMachineRestockSyncReminderDto.SupportView;
        }

        // And then the plan, before the connection is read at all (issue #967): the sync this banner
        // reminds an operator to run is itself refused by the credential gate, so a business that has
        // not bought the provider is told the reminder does not apply rather than sent to an action
        // the application will refuse.
        if (!await _plan
                .AllowsAsync(PlanFeature.SalesProviderIntegration, cancellationToken)
                .ConfigureAwait(false))
        {
            return HiddenMachineRestockSyncReminderDto.PlanRequired;
        }

        var connection = await _connections.FindAsync(cancellationToken).ConfigureAwait(false);
        if (connection is null || !NayaxConnectionStatusGate.AllowsOrdinaryOperations(connection.Status))
        {
            return HiddenMachineRestockSyncReminderDto.NayaxNotConnected;
        }

        // Resolved once, before the aggregate read that needs the pending window's start (issue
        // #948), so every row on the banner - and the window itself - is measured against one
        // "today".
        var nowUtc = _clock.UtcNow;
        var businessToday = _businessCalendar.ToBusinessDate(nowUtc);
        var pendingFromBusinessDate = MachineRestockSyncReminderPolicy.PendingFromBusinessDate(businessToday);
        var pendingFromUtc = _businessCalendar.StartOfBusinessDayUtc(pendingFromBusinessDate);

        var machines = await _coverage.GetProviderMachineCoverageAsync(pendingFromUtc, cancellationToken)
            .ConfigureAwait(false);
        if (machines.Count == 0)
        {
            return HiddenMachineRestockSyncReminderDto.NoProviderMachines;
        }

        var needingAttention = machines
            .Select(machine => Evaluate(machine, connection.CredentialRevision, businessToday))
            .Where(machine => MachineRestockSyncReminderPolicy.NeedsAttention(
                machine.IsFetchOverdue, machine.KnownPendingEventCount))
            .ToList();

        return new MachineRestockSyncReminderDto(
            nowUtc,
            businessToday,
            MachineRestockSyncReminderPolicy.OverdueAfterDays,
            MachineRestockSyncReminderPolicy.DefaultFromBusinessDate(businessToday),
            MachineRestockSyncReminderPolicy.PendingLookbackDays,
            pendingFromBusinessDate,
            Availability(connection),
            needingAttention);
    }

    /// <summary>
    /// One machine's row. Coverage recorded under a superseded credential revision is read as no
    /// recorded sync at all, which is what keeps a replaced token from inheriting the previous
    /// one's freshness.
    /// </summary>
    private MachineRestockSyncReminderMachineDto Evaluate(
        MachineRestockSyncCoverageRecord machine, int currentCredentialRevision, DateTime businessToday)
    {
        var lastSyncedAtUtc =
            MachineRestockSyncReminderPolicy.IsCoverageCurrent(machine.CredentialRevision, currentCredentialRevision)
                ? machine.LastSuccessfulSyncStartedAtUtc
                : null;

        var lastSyncedBusinessDate = lastSyncedAtUtc is { } syncedAtUtc
            ? _businessCalendar.ToBusinessDate(syncedAtUtc)
            : (DateTime?)null;

        var oldestPendingBusinessDate = machine.OldestKnownPendingEventAtUtc is { } oldestPendingAtUtc
            ? _businessCalendar.ToBusinessDate(oldestPendingAtUtc)
            : (DateTime?)null;

        return new MachineRestockSyncReminderMachineDto(
            machine.LocalMachineId,
            machine.MachineName,
            lastSyncedAtUtc,
            lastSyncedBusinessDate,
            MachineRestockSyncReminderPolicy.DaysSinceLastSuccessfulSync(lastSyncedBusinessDate, businessToday),
            MachineRestockSyncReminderPolicy.IsFetchOverdue(lastSyncedBusinessDate, businessToday),
            machine.KnownPendingEventCount,
            machine.OldestKnownPendingEventAtUtc,
            oldestPendingBusinessDate,
            MachineRestockSyncReminderPolicy.HasPendingWorkBeforeDefaultFrom(
                oldestPendingBusinessDate, businessToday));
    }

    /// <summary>
    /// What the stored connection says about the machine alerts read, with no Nayax call made; see
    /// <see cref="MachineRestockSyncAvailability"/>. <c>PendingPermissions</c>, and a <c>Ready</c>
    /// connection whose machine-scoped checks are still unverified (issue #537), are both
    /// unconfirmed: this application has no completed check that covered
    /// <c>GET /v1/machines/{MachineID}/lastAlerts</c>, so the banner must not present Sync Restock
    /// as a working action.
    /// </summary>
    private static string Availability(NayaxConnection connection) =>
        connection.Status == NayaxConnectionStatus.Ready && !connection.MachineChecksPending
            ? MachineRestockSyncAvailability.Confirmed
            : MachineRestockSyncAvailability.NotConfirmed;
}
