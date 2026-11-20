namespace Inventory.Domain.Subscriptions;

/// <summary>
/// The <strong>beta Pro grant</strong> an approved business receives (issue #1258, child of #965,
/// after #966 and #982): what it grants, why, and the one question of whether an approval writes it
/// at all.
///
/// <para>It exists because the landing page and Terms of Use §3 promise that "Pro access is free
/// during our public beta". Once plan enforcement is switched on, a business approved after the
/// grandfathering cutoff would otherwise resolve to Free, so the promise would depend on a Super
/// Admin remembering a manual grant for every business they approve. This rule is that promise
/// recorded per business instead.</para>
///
/// <para>It is a pure rule: no clock, no database, no configuration read and no actor. The setting
/// arrives as a boolean the composition root resolved
/// (<c>Inventory.Application.Subscriptions.IBetaProGrantOnApprovalFlag</c>), the business's grants
/// arrive as they are stored, and the instant the grant starts from is the approval's own, supplied
/// by the use case.</para>
///
/// <para><strong>What it deliberately does not decide.</strong> What the granted plan then
/// <em>entitles</em> - that is <see cref="EffectiveEntitlementPolicy"/>, which reads a grant's plan
/// and period and never its source, so a beta grant entitles exactly what a complimentary one does -
/// and anything about ending the beta, which is a human decision through the Super Admin surface of
/// issue #982.</para>
/// </summary>
public static class BetaProGrantPolicy
{
    /// <summary>
    /// The plan a beta grant is for. Pro, and not a configurable value: the promise that was made
    /// publicly is Pro, and a setting that could lower it would be a way to quietly withdraw it.
    /// </summary>
    public const SubscriptionPlan GrantedPlan = SubscriptionPlan.Pro;

    /// <summary>Why the grant exists, recorded in the column rather than in the reason text.</summary>
    public const ManualPlanGrantSource GrantSource = ManualPlanGrantSource.Beta;

    /// <summary>
    /// The reason recorded on the grant row and on its audit row. It is fixed rather than typed,
    /// because no person is typing it: the reason every one of these grants exists is the same
    /// sentence, and a reason an operator could vary would make the trail harder to read rather than
    /// easier.
    /// </summary>
    public const string GrantReason = "Public beta introductory Pro access";

    /// <summary>
    /// Whether approving a business application writes the beta grant.
    ///
    /// <para><strong>Two conditions, and both are deliberately narrow.</strong> The setting is on,
    /// and the business holds <em>no</em> manual grant that the Super Admin surface administers. The
    /// second is stricter than "no grant that currently applies": a business that already holds a
    /// grant - active, or one a Super Admin has already ended - is left exactly as it is, because the
    /// alternative is either a second grant row nobody decided or an existing decision being written
    /// over. A business whose grant was ended before it was ever approved is a state nothing in this
    /// application produces; if it ever arises, a Super Admin grants Pro by hand through #982, which
    /// is the surface that owns that record.</para>
    ///
    /// <para>The grandfathered grant of issue #966 is not among these grants and cannot be: it is
    /// written only for a business that was already trading before the recorded cutoff, which a
    /// business waiting for its application to be decided never was.</para>
    /// </summary>
    /// <param name="settingEnabled">
    /// Whether <c>Subscriptions:BetaProGrantOnApproval</c> is on in this deployment. Off - absent,
    /// blank or unparseable included - means approval behaves exactly as it did before this rule
    /// existed.
    /// </param>
    /// <param name="existingGrants">
    /// Every manual grant the business holds that the plan-grant surface administers, in any order.
    /// </param>
    public static bool GrantsOnApproval(
        bool settingEnabled,
        IReadOnlyList<ManualPlanGrantState> existingGrants)
    {
        ArgumentNullException.ThrowIfNull(existingGrants);

        return settingEnabled && existingGrants.Count == 0;
    }

    /// <summary>
    /// The period a beta grant is written with: from the approval instant inclusive, with
    /// <strong>no recorded end</strong>.
    ///
    /// <para>Open-ended is the decision of 14 November 2026 on #1258 and is what makes switching the
    /// setting off harmless: it stops new grants and ends none, so a business keeps the Pro it was
    /// promised until a Super Admin ends the grant deliberately. An end date computed from "when the
    /// beta ends" would be this rule guessing a commercial date, and every business's grant would
    /// carry whichever guess was current on the day it was approved.</para>
    /// </summary>
    /// <param name="approvedAtUtc">
    /// The approval instant, sampled inside the approval's own write transaction.
    /// </param>
    public static EntitlementPeriod PeriodFrom(DateTime approvedAtUtc) => new(approvedAtUtc, null);
}
