namespace Inventory.Domain.Tenancy;

/// <summary>
/// The deterministic rules of switching a business off and on again, and of reaching its members
/// while it is off (issue #545, one slice of #509 under #328): which stored status each operation
/// may be asked of, and which sentence a refusal carries.
///
/// <para>Like <see cref="BusinessDecisionPolicy"/> beside it, it holds no clock, no database and no
/// configuration - every answer is a function of the status handed in - and it decides nothing about
/// <em>who</em> may ask. That is the configured platform administrator and nothing else, enforced at
/// the HTTP boundary by <c>PlatformAdminPolicy</c> (issue #336); an authority rule written here
/// would be a second answer to a question that already has one.</para>
///
/// <para><strong>Each transition names exactly one status it starts from, and the deny direction is
/// the default.</strong> A business this code cannot read a status for - an unfilled column reading
/// <c>0</c>, or a value a newer deployment wrote - may be neither deactivated nor reactivated, and
/// its members may be neither listed nor released, for the same reason
/// <see cref="BusinessMembershipResolutionPolicy"/> denies it access: a state nobody declared is not
/// a state anything may be decided from. Deciding a <see cref="BusinessStatus.Pending"/> application
/// stays <see cref="BusinessDecisionPolicy"/>'s, and a <see cref="BusinessStatus.Rejected"/>
/// application is never revived by this path.</para>
///
/// <para><strong>Why releasing a member is tied to the deactivated status.</strong> A trading
/// business's membership list is its own Owner's to manage (issue #533), and the platform
/// administrator is deliberately not a second Owner of every business. The one situation an Owner
/// cannot resolve is a business that has been switched off: nothing resolves it, so its Owner can
/// act on nothing, and a member left attached to it could never join anywhere else
/// (<see cref="MembershipEligibility"/>). That is the gap this operation exists for, and binding it
/// to <see cref="BusinessStatus.Deactivated"/> is what keeps it from becoming a general power over
/// other people's members.</para>
/// </summary>
public static class BusinessLifecyclePolicy
{
    /// <summary>
    /// The refusal for a business that is not trading, so there is nothing to switch off. It is one
    /// sentence for a pending application, a refused one, an already-deactivated business and an
    /// undeclared stored value alike: the fact the caller needs is that this business is not in the
    /// state the operation starts from.
    ///
    /// <para>Unlike <see cref="BusinessDecisionPolicy"/>'s refusals, the sentences here may name the
    /// business's own lifecycle state. Those are read by an applicant through their account state;
    /// these reach the platform administrator alone, whose directory already reports every
    /// business's status - so withholding it would hide nothing and explain nothing.</para>
    /// </summary>
    public const string NotActiveMessage = "This business is not active, so it cannot be deactivated.";

    /// <summary>The refusal for a business that is not deactivated, so there is nothing to switch on.</summary>
    public const string NotDeactivatedMessage =
        "This business is not deactivated, so it cannot be reactivated.";

    /// <summary>
    /// The refusal for listing or releasing the members of a business that is not deactivated. The
    /// two operations share one sentence because they rest on one fact, and neither reports a single
    /// member of a business it refuses - so a business's membership list is never confirmed to exist
    /// by the way the refusal is worded.
    /// </summary>
    public const string MembersUnavailableMessage =
        "This business is not deactivated, so its members cannot be listed or released.";

    /// <summary>
    /// The refusal for reactivating a business whose memberships no longer include an active Owner.
    ///
    /// <para>It is deliberately not <see cref="BusinessOwnerRetention.LastActiveOwnerMessage"/>.
    /// That sentence tells the reader to give another member the Owner role first, which is
    /// something only an Owner inside the business can do - and a business that is still deactivated
    /// has no reachable Owner at all, so the advice would send the caller nowhere. The fact here is
    /// about the business: reactivating it would produce a trading tenant nobody can administer,
    /// which has no repair path short of a human editing the database.</para>
    /// </summary>
    public const string NoActiveOwnerMessage =
        "This business has no active Owner, so it cannot be reactivated. Releasing every Owner of a "
            + "deactivated business is permanent until a membership is restored by other means.";

    /// <summary>
    /// Whether a business in <paramref name="status"/> may be deactivated: only
    /// <see cref="BusinessStatus.Active"/> may.
    /// </summary>
    public static bool CanDeactivate(BusinessStatus status) => status == BusinessStatus.Active;

    /// <summary>
    /// Whether a business in <paramref name="status"/> may be reactivated: only
    /// <see cref="BusinessStatus.Deactivated"/> may. Whether it still <em>has</em> an active Owner is
    /// <see cref="BusinessOwnerRetention"/>'s separate rule, judged on the memberships the business
    /// holds inside the write transaction.
    /// </summary>
    public static bool CanReactivate(BusinessStatus status) => status == BusinessStatus.Deactivated;

    /// <summary>
    /// Whether the members of a business in <paramref name="status"/> may be listed and released by
    /// the platform administrator: only while it is <see cref="BusinessStatus.Deactivated"/>.
    /// </summary>
    public static bool AllowsMemberAccess(BusinessStatus status) => status == BusinessStatus.Deactivated;
}
