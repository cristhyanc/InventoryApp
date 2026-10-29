using Inventory.Domain.Tenancy;

namespace Inventory.Domain.Subscriptions;

/// <summary>
/// One business as the grandfathering step reads it (issue #966): the four stored facts the
/// eligibility rule is decided from, and nothing else.
/// </summary>
/// <param name="BusinessId">
/// The business's own key, from the database the command read it in - never from a command line.
/// </param>
/// <param name="Status">Its lifecycle status (issue #540).</param>
/// <param name="CreatedAtUtc">When the business record was created.</param>
/// <param name="DecidedAtUtc">
/// When its application was approved or refused, or <see langword="null"/> when it never went
/// through one - the existing business, and any <c>bootstrap-business</c> created. It is what makes
/// "its status <em>at the cutoff</em>" answerable from stored data: a business whose decision was
/// recorded at or after the cutoff was still a pending application at the cutoff, however active it
/// is by the time a rerun reads it.
/// </param>
public sealed record GrandfatheringCandidate(
    int BusinessId,
    BusinessStatus Status,
    DateTime CreatedAtUtc,
    DateTime? DecidedAtUtc);

/// <summary>
/// Which businesses the grandfathering step grants Pro to (issue #966, decided 29 October 2026 on
/// #965).
///
/// <para><strong>Eligible:</strong> a business created before the recorded cutoff whose status at
/// the cutoff was <see cref="BusinessStatus.Active"/> or <see cref="BusinessStatus.Deactivated"/> -
/// a business that was trading, or had traded and been offboarded. A deactivated business is
/// included deliberately: its records are kept rather than deleted, and reactivating it must not
/// cost it the plan every other established business was given.</para>
///
/// <para><strong>Not eligible:</strong> a <see cref="BusinessStatus.Pending"/> application, a
/// <see cref="BusinessStatus.Rejected"/> one, and anything created at or after the cutoff. <strong>A
/// pending application approved later starts on Free</strong>, and that holds on a rerun: the rule
/// reads the recorded decision instant, so a business approved after the cutoff is still ineligible
/// when the command is run again, even though its status is <see cref="BusinessStatus.Active"/> by
/// then. Without that, the second run of a "safe to rerun" command would hand Pro to a business the
/// first run correctly refused.</para>
///
/// <para>Eligibility is decided from the <em>recorded</em> cutoff alone, which the first run fixes
/// and no later run moves. The rule is a pure function of the four facts above and that instant, so
/// a rerun, a resumed run and a dry run all agree about every business.</para>
/// </summary>
public static class GrandfatheringEligibilityPolicy
{
    /// <summary>The plan an eligible business is granted, with no end date.</summary>
    public const SubscriptionPlan GrandfatheredPlan = SubscriptionPlan.Pro;

    /// <summary>
    /// Whether a business is grandfathered, and when it is not, which rule refused it.
    ///
    /// <para>The reason is part of the answer rather than something the command infers afterwards:
    /// the operator running this step has to be able to read why a business was left on Free, and a
    /// reason derived a second time from the same columns is a second rule that can disagree with
    /// the first.</para>
    /// </summary>
    public enum Decision
    {
        /// <summary>Grandfathered: created before the cutoff and trading, or offboarded, at it.</summary>
        Eligible = 10,

        /// <summary>Created at or after the cutoff - a new business, which starts on Free.</summary>
        CreatedAtOrAfterCutoff = 20,

        /// <summary>
        /// A pending or rejected application, or a status this code does not declare: it was not
        /// trading at the cutoff.
        /// </summary>
        NotTradingAtCutoff = 30,

        /// <summary>
        /// Its application was approved or refused at or after the cutoff, so it was still a pending
        /// application at the cutoff however active it is now. This is the rule that keeps a rerun
        /// from granting Pro to a business the first run correctly refused.
        /// </summary>
        DecidedAtOrAfterCutoff = 40,
    }

    /// <summary>
    /// The statuses that were trading, or had traded, at the cutoff. Written out rather than
    /// derived as "not pending and not rejected", so a lifecycle status declared later is not
    /// granted Pro on the day it is invented.
    /// </summary>
    private static readonly BusinessStatus[] EligibleStatuses =
    [
        BusinessStatus.Active,
        BusinessStatus.Deactivated,
    ];

    /// <summary>Whether <paramref name="candidate"/> is to be granted the grandfathered plan.</summary>
    /// <param name="candidate">The business, as the command read it.</param>
    /// <param name="cutoffUtc">
    /// The cutoff instant the grandfathering record holds, fixed by the first run.
    /// </param>
    public static bool IsEligible(GrandfatheringCandidate candidate, DateTime cutoffUtc) =>
        Decide(candidate, cutoffUtc) == Decision.Eligible;

    /// <summary>
    /// The eligibility decision, with the refusing rule when there is one. The rules are applied in
    /// the order below, so a business refused by several of them reports the first.
    /// </summary>
    public static Decision Decide(GrandfatheringCandidate candidate, DateTime cutoffUtc)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        if (candidate.CreatedAtUtc >= cutoffUtc)
        {
            return Decision.CreatedAtOrAfterCutoff;
        }

        if (!EligibleStatuses.Contains(candidate.Status))
        {
            return Decision.NotTradingAtCutoff;
        }

        // A decision recorded at or after the cutoff means the business was a pending application
        // at the cutoff. Its status today says nothing about what it was then, and the cutoff is
        // what this step grants by.
        return candidate.DecidedAtUtc is null || candidate.DecidedAtUtc < cutoffUtc
            ? Decision.Eligible
            : Decision.DecidedAtOrAfterCutoff;
    }
}
