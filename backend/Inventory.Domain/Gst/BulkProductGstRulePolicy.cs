using Inventory.Domain.Exceptions;

namespace Inventory.Domain.Gst;

/// <summary>
/// The rules of configuring a GST rule on every product that currently has none (issue #1076): what
/// may be asked for, which product it reaches, and when a confirmed change is refused because the
/// set it was confirmed against has moved.
///
/// <para><strong>It is <see cref="GstRules"/>'s companion, not a second vocabulary.</strong> What a
/// rule may <em>be</em> is that class's, and a product's rule still means exactly what it meant
/// before: configuration that classifies nothing by itself, read only by the historical
/// Preview/Apply maintenance workflow (issue #433). What is here is only what doing it to many
/// products at once adds.</para>
///
/// <para><strong>"No rule" is not a target.</strong> <see cref="GstRules.None"/> is a legitimate
/// value for the single-product resource - it is how one product's rule is cleared - but it is not
/// something this action can be asked for: the eligible products are exactly the ones already
/// carrying it, so setting it would write nothing while reporting that something was configured.
/// The operator chooses <see cref="GstClassification.Taxable"/> or
/// <see cref="GstClassification.GstFree"/>, and the choice is never made for them (that is the
/// issue's own rule: a tax treatment is a decision, never a default).</para>
///
/// <para><strong>Eligibility is read twice, and the second read is the one that counts.</strong>
/// <see cref="IsEligible"/> is what the preview counts and what the write's own predicate applies
/// inside its transaction, so a product given an explicit rule after the preview is simply not
/// written - an explicit rule is somebody's decision and is never replaced by this action.
/// <see cref="RequireUnchangedEligibleCount"/> is the separate guarantee that the operator approved
/// the set that is about to be written: a count that has moved means products were configured,
/// created or deleted since they looked, so the change is refused and a fresh confirmation asked
/// for rather than applied to a set nobody saw.</para>
///
/// <para>It stores nothing and reads nothing, like every policy in this folder: both counts are
/// read inside the write's own transaction and handed here.</para>
/// </summary>
public static class BulkProductGstRulePolicy
{
    /// <summary>
    /// What a caller asking for a target outside the two is told. It names the two targets rather
    /// than the whole classification vocabulary, because "no rule" is a declared classification and
    /// still not something this action can set.
    /// </summary>
    public const string UnsupportedTargetMessage =
        "A bulk GST rule change must set either Taxable or GstFree. 'No rule' is what the products "
            + "being changed already carry, so it is not a target, and no other value is a GST rule.";

    /// <summary>
    /// What a caller whose request carried no confirmed count at all is told. A <em>validation</em>
    /// failure and not a conflict, the way the master-product edit tells those two apart: the
    /// request is incomplete rather than stale, and the two answers must stay different because one
    /// asks for the field and the other asks for a fresh confirmation.
    /// </summary>
    public const string ExpectedEligibleCountRequiredMessage =
        "A bulk GST rule change must carry the eligible product count it was confirmed against.";

    /// <summary>
    /// What an operator whose confirmed count no longer matches the database is told: somebody -
    /// another tab, another member - configured, created or deleted a product since this was
    /// previewed, so the set on screen is not the set that would be written.
    /// </summary>
    public const string EligibleCountChangedMessage =
        "The products with no GST rule changed while this was being confirmed, so nothing was "
            + "changed. Refresh the count and confirm again before applying.";

    /// <summary>
    /// Whether a product carrying <paramref name="storedRule"/> may be configured by this action:
    /// only while it carries no rule at all.
    ///
    /// <para>An explicit <see cref="GstClassification.GstFree"/> is deliberately <em>not</em>
    /// eligible even when the target is <c>GstFree</c>. "GST-free" and "nobody has said" are
    /// different statements, and the rule this action exists to preserve is that an explicit one is
    /// never overwritten - not even by a change that would happen to agree with it.</para>
    /// </summary>
    public static bool IsEligible(GstClassification storedRule) => storedRule == GstRules.None;

    /// <summary>
    /// The target rule, refused unless it is one of the two a bulk change may set.
    /// </summary>
    /// <exception cref="DomainValidationException">
    /// The value is <see cref="GstRules.None"/> or outside the declared vocabulary entirely - which
    /// is what an arbitrary JSON number binds to. Nothing is written.
    /// </exception>
    public static GstClassification RequireTarget(GstClassification target) =>
        GstRules.IsSupportedRule(target) && target != GstRules.None
            ? target
            : throw new DomainValidationException(UnsupportedTargetMessage);

    /// <summary>The confirmed eligible count a request must carry, refused when it carries none.</summary>
    /// <exception cref="DomainValidationException">
    /// The request carried no count. Nothing is written.
    /// </exception>
    public static int RequireSubmittedEligibleCount(int? expectedEligibleCount) =>
        expectedEligibleCount
            ?? throw new DomainValidationException(ExpectedEligibleCountRequiredMessage);

    /// <summary>
    /// Refuses a change whose confirmed eligible count is not the one the database now holds.
    /// </summary>
    /// <param name="expectedCount">The count the operator confirmed.</param>
    /// <param name="actualCount">
    /// The count the write's own transaction read, which is what makes the comparison authoritative
    /// rather than a guess about what happened in between.
    /// </param>
    /// <exception cref="DomainConflictException">They differ. Nothing is written.</exception>
    public static void RequireUnchangedEligibleCount(int expectedCount, int actualCount)
    {
        if (expectedCount != actualCount)
        {
            throw new DomainConflictException(EligibleCountChangedMessage);
        }
    }
}
