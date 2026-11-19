namespace Inventory.Application.Subscriptions;

/// <summary>
/// Whether approving a business application also grants that business open-ended Pro for the public
/// beta (issue #1258): the <c>Subscriptions:BetaProGrantOnApproval</c> setting, read through a port so
/// the Application layer owns the <em>decision</em> while the InventoryApi composition root owns the
/// configuration source.
///
/// <para>It follows <see cref="ISubscriptionEnforcementFlag"/> exactly, including why.
/// <strong>It is off unless a deployment says otherwise</strong>: an absent section, a blank value and
/// an unparseable one are all off, because a setting nobody typed must never start writing a plan
/// grant for every business that is approved. While it is off, approval behaves exactly as it did
/// before this rule existed.</para>
///
/// <para>It is a <em>separate</em> flag from plan enforcement on purpose. The two answer different
/// questions - "is a plan enforced at all?" and "does an approval record the beta's promise?" - and
/// they are switched on at different times: the grants have to exist before enforcement starts, or
/// the businesses holding them would be enforced down to Free.</para>
///
/// <para>It is read once at startup rather than per request, for the reason the enforcement flag is:
/// switching it on is a deliberate release step, not something that should take effect from a
/// half-saved configuration file mid-request. A request can neither see nor influence it, and
/// switching it off later stops new grants only - a grant already written stays until a Super Admin
/// ends it through issue #982's surface.</para>
/// </summary>
public interface IBetaProGrantOnApprovalFlag
{
    /// <summary>Whether an approval writes the beta Pro grant.</summary>
    bool IsEnabled { get; }
}
