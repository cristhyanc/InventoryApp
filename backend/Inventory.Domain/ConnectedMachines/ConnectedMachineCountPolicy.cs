using Inventory.Domain.Nayax;
using Inventory.Domain.Subscriptions;

namespace Inventory.Domain.ConnectedMachines;

/// <summary>
/// How well known a business's connected-machine count currently is (issue #978). Three distinct
/// answers, never labels on each other, in the shape the report availability model of issue #730
/// established: a figure and the state of the evidence behind it travel together.
/// </summary>
public enum ConnectedMachineCountState
{
    /// <summary>Today's UTC day has a snapshot for the current account series.</summary>
    Current = 10,

    /// <summary>
    /// The last complete snapshot of the current series is older than today, and it remains the
    /// count - reported with its age, never silently as today's figure.
    /// </summary>
    Stale = 20,

    /// <summary>
    /// There is no count to report: the connection cannot be used, or the current account has never
    /// been counted. <strong>Never reported as zero</strong> - a count nobody could take is not a
    /// fleet with no machines in it.
    /// </summary>
    Unavailable = 30,
}

/// <summary>
/// Why a connected-machine count is unavailable (issue #978). It is reported so a page, an operator
/// and a billing question can tell "nobody could read this account" from "this account has not been
/// counted yet".
/// </summary>
public enum ConnectedMachineCountUnavailableReason
{
    /// <summary>The count is available; see <see cref="ConnectedMachineCountState"/>.</summary>
    None = 0,

    /// <summary>
    /// The business has stored no Nayax credentials at all
    /// (<see cref="NayaxConnectionStatus.NotConfigured"/>), so no fleet read is possible.
    /// </summary>
    NotConnected = 10,

    /// <summary>
    /// The stored credentials are known not to work
    /// (<see cref="NayaxConnectionStatus.NeedsAttention"/>). No call is made - an operator has to
    /// fix the connection first.
    /// </summary>
    ConnectionNeedsAttention = 20,

    /// <summary>The connection is usable but no complete read has been recorded yet.</summary>
    NeverCounted = 30,

    /// <summary>
    /// A different Nayax account has been saved since the last snapshot, and the new account has not
    /// been counted yet. The previous account's snapshots stay exactly as they are and are
    /// deliberately not reported as this account's count.
    /// </summary>
    AccountNotYetCounted = 40,
}

/// <summary>
/// What a business's connected machines cost it, as <c>GET /api/me/access</c> reports it beside the
/// plan (issue #978, consumed by the billing of #969 and the plan screens of #970).
///
/// <para><strong>Being over the allowance is billed, never blocked.</strong>
/// <see cref="MachinesOverAllowance"/> is an amount to charge for, and no code may read it as a
/// limit: linking a machine, importing sales, a report and the pick list are unaffected by it, and
/// nothing here removes a link or any history.</para>
/// </summary>
/// <param name="IncludedByPlan">
/// How many connected machines the effective plan includes - five on Pro, none on a plan with no
/// Nayax connection (<c>PlanCatalogue</c>, which is the one place that number is stated).
/// </param>
/// <param name="PurchasedAdditional">
/// How many extra connected machines the business has bought beyond the included ones. It is
/// <c>0</c> until the billing integration of #969 records a purchase: nothing in this slice writes
/// or infers one, and an unrecorded purchase is none rather than a guess.
/// </param>
/// <param name="Allowance">
/// <paramref name="IncludedByPlan"/> plus <paramref name="PurchasedAdditional"/> - the number the
/// count is compared against.
/// </param>
/// <param name="Count">
/// The connected machines the current account series was last counted at, or <see langword="null"/>
/// while <paramref name="State"/> is <see cref="ConnectedMachineCountState.Unavailable"/>. Null, never
/// zero.
/// </param>
/// <param name="AsOfDateUtc">
/// The UTC day the reported count was taken for, or <see langword="null"/> when there is none.
/// </param>
/// <param name="AgeInDays">
/// How many UTC days older than today the reported count is: <c>0</c> while it is current, the real
/// age while it is stale, and <see langword="null"/> while there is no count.
/// </param>
/// <param name="State">How well known the count is.</param>
/// <param name="UnavailableReason">
/// Why there is no count, or <see cref="ConnectedMachineCountUnavailableReason.None"/> when there is
/// one.
/// </param>
/// <param name="MachinesOverAllowance">
/// How many counted machines are above <paramref name="Allowance"/>, never negative, and
/// <see langword="null"/> while there is no count - because "none over" and "we do not know" are
/// different answers and only one of them is a reason to bill nothing.
/// </param>
public sealed record ConnectedMachineEntitlement(
    int IncludedByPlan,
    int PurchasedAdditional,
    int Allowance,
    int? Count,
    DateTime? AsOfDateUtc,
    int? AgeInDays,
    ConnectedMachineCountState State,
    ConnectedMachineCountUnavailableReason UnavailableReason,
    int? MachinesOverAllowance);

/// <summary>
/// The single rule that turns a plan, a stored connection status and the snapshots a business holds
/// into the connected-machine answer above (issue #978).
///
/// <para>It is pure: no clock, no database, no configuration. The instant comes from the application
/// clock through the use case that calls it, and every snapshot is a stored fact.</para>
///
/// <para><strong>The order of the rules is the design.</strong>
/// <list type="number">
///   <item>An unusable connection - nothing stored, or credentials known not to work - is
///   <em>unavailable</em>, whatever snapshots exist. No call is made for it, and the previous
///   account's or the broken connection's figures are not presented as the current count.</item>
///   <item>Otherwise the count is the latest snapshot of the <em>current</em> account series
///   (<see cref="ConnectedMachineSnapshotPolicy.Latest"/>, which is also where "the day's count is
///   the later series' snapshot" lives). A snapshot belonging to a superseded series is not this
///   account's count.</item>
///   <item>Today's snapshot is <em>current</em>; an older one stays the count and is reported
///   <em>stale</em> with its age. A failed, partial or paginated-incomplete read changes neither,
///   because it stored nothing.</item>
/// </list></para>
/// </summary>
public static class ConnectedMachineCountPolicy
{
    /// <summary>
    /// How many extra connected machines a business has bought while nothing records a purchase -
    /// which is every business until #969 ships. Named so the one caller says what it means rather
    /// than passing a bare <c>0</c>.
    /// </summary>
    public const int NoPurchasedAdditionalMachines = 0;

    /// <summary>
    /// Decides the connected-machine entitlement.
    /// </summary>
    /// <param name="plan">
    /// The business's effective plan, which must be declared (<see cref="SubscriptionPlans.IsDeclared"/>):
    /// the allowance comes from its catalogue entry, and there is no allowance to invent for a plan
    /// this code does not know.
    /// </param>
    /// <param name="purchasedAdditionalMachines">
    /// Extra machines the business has bought, from the billing record (#969). Negative is treated as
    /// none; a purchase nobody recorded is not an allowance.
    /// </param>
    /// <param name="connectionStatus">
    /// The business's stored Nayax connection status, or <see cref="NayaxConnectionStatus.NotConfigured"/>
    /// when it has no connection row at all.
    /// </param>
    /// <param name="snapshots">
    /// The snapshots to decide from - the rows of the business's latest recorded day, which is one
    /// row except on a day its Nayax account changed.
    /// </param>
    /// <param name="currentAccountSeries">
    /// The account series the business's connection is on now. A snapshot from any other series is
    /// evidence about an account the business has left.
    /// </param>
    /// <param name="todayUtc">The current UTC day, from the application clock.</param>
    public static ConnectedMachineEntitlement Decide(
        SubscriptionPlan plan,
        int purchasedAdditionalMachines,
        NayaxConnectionStatus connectionStatus,
        IReadOnlyList<ConnectedMachineSnapshot> snapshots,
        int currentAccountSeries,
        DateTime todayUtc)
    {
        ArgumentNullException.ThrowIfNull(snapshots);

        var included = PlanCatalogue.For(plan).IncludedConnectedMachines;
        var purchased = Math.Max(0, purchasedAdditionalMachines);
        var allowance = included + purchased;

        if (!ConnectedMachineSnapshotPolicy.MayReadFleet(connectionStatus))
        {
            return Unavailable(
                included,
                purchased,
                allowance,
                connectionStatus == NayaxConnectionStatus.NeedsAttention
                    ? ConnectedMachineCountUnavailableReason.ConnectionNeedsAttention
                    : ConnectedMachineCountUnavailableReason.NotConnected);
        }

        if (ConnectedMachineSnapshotPolicy.Latest(snapshots) is not { } latest)
        {
            return Unavailable(
                included,
                purchased,
                allowance,
                ConnectedMachineCountUnavailableReason.NeverCounted);
        }

        if (latest.AccountSeries != currentAccountSeries)
        {
            return Unavailable(
                included,
                purchased,
                allowance,
                ConnectedMachineCountUnavailableReason.AccountNotYetCounted);
        }

        var day = ConnectedMachineSnapshotDay.For(todayUtc);
        var ageInDays = Math.Max(0, (int)(day - latest.DateUtc).TotalDays);

        return new ConnectedMachineEntitlement(
            included,
            purchased,
            allowance,
            latest.MachineCount,
            latest.DateUtc,
            ageInDays,
            ageInDays == 0 ? ConnectedMachineCountState.Current : ConnectedMachineCountState.Stale,
            ConnectedMachineCountUnavailableReason.None,
            Math.Max(0, latest.MachineCount - allowance));
    }

    private static ConnectedMachineEntitlement Unavailable(
        int included,
        int purchased,
        int allowance,
        ConnectedMachineCountUnavailableReason reason) =>
        new(
            included,
            purchased,
            allowance,
            Count: null,
            AsOfDateUtc: null,
            AgeInDays: null,
            ConnectedMachineCountState.Unavailable,
            reason,
            MachinesOverAllowance: null);
}
