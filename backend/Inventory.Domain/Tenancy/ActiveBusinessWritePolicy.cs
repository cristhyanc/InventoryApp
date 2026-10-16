namespace Inventory.Domain.Tenancy;

/// <summary>
/// The rule that a write <em>into</em> a business requires that business to still be trading
/// (issue #680).
///
/// <para><strong>Why it is a rule at all, when the middleware already refuses.</strong>
/// <see cref="BusinessMembershipResolutionPolicy"/> resolves nothing for a business whose status is
/// not <see cref="BusinessStatus.Active"/>, so a member of a deactivated business is refused on
/// their very next request. What that cannot cover is the request that was already past the
/// middleware when the deactivation committed: its business id was resolved while the business was
/// still <see cref="BusinessStatus.Active"/>, and without this rule its insert or update lands on a
/// business that has been switched off. The consequence the issue names is an invite: a `Pending`
/// seven-day invite committed onto a deactivated business is redeemable again the moment the
/// business is reactivated, which is exactly the way back in that cancelling every live invite on
/// deactivation exists to close.</para>
///
/// <para><strong>Only <see cref="BusinessStatus.Active"/> permits a write</strong>, written as
/// "is <see cref="BusinessStatus.Active"/>" rather than as a list of the statuses that refuse - so a
/// pending business, a rejected one, a deactivated one, an unfilled column reading <c>0</c> and a
/// value a newer deployment wrote are all refused by construction rather than by a case somebody has
/// to remember to add. It is the same shape, and the same deny-by-default direction, as the
/// resolution rule and as <see cref="BusinessLifecyclePolicy"/>'s status gates.</para>
///
/// <para>Where the rule is applied is <c>Inventory.Application.Tenancy.MembershipWriteGuard</c>,
/// inside the <c>BEGIN IMMEDIATE</c> write transaction it opens, so the answer cannot be overtaken
/// by a deactivation between the check and the commit (docs/architecture.md § One active membership
/// per identity).</para>
/// </summary>
public static class ActiveBusinessWritePolicy
{
    /// <summary>
    /// The refusal a write into a business that is no longer trading carries. It is one sentence for
    /// a deactivated business, a pending or rejected one and an undeclared stored value alike: the
    /// fact the caller needs is that their change was not stored, and the status itself is already
    /// what their next request is refused by.
    ///
    /// <para>It names no business, member or role, and it deliberately does not advise a repair -
    /// nobody inside a business that has been switched off can switch it back on.</para>
    /// </summary>
    public const string NotActiveMessage = "This business is no longer active, so the change was not saved.";

    /// <summary>
    /// Whether a business in <paramref name="status"/> may be written into: only
    /// <see cref="BusinessStatus.Active"/> may.
    /// </summary>
    public static bool AllowsWrite(BusinessStatus status) => status == BusinessStatus.Active;
}
