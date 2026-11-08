namespace Inventory.Domain.NayaxSqs;

/// <summary>
/// What a business Owner may decide about a flagged queue-delivered sale (issue #1105).
///
/// <para>No value is <c>0</c>, for the reason <see cref="NayaxSqsVerification"/> declares none.</para>
/// </summary>
public enum NayaxSaleOwnerDecision
{
    /// <summary>
    /// "Use Nayax's values": replace the stored machine, amount, instant and product with the
    /// provider's own record from the latest comparison. Only for a <see cref="NayaxSqsVerification.Conflict"/>.
    /// </summary>
    UseNayaxValues = 10,

    /// <summary>"Keep the SQS values": the stored values stand. Only for a conflict; reason required.</summary>
    KeepSqsValues = 20,

    /// <summary>"Don't count this sale". For a conflict or a sale needing investigation; reason required.</summary>
    DoNotCount = 30,

    /// <summary>
    /// "This sale is genuine". Only for a sale needing investigation, whose values no evidence
    /// contradicts; reason required.
    /// </summary>
    Genuine = 40,
}

/// <summary>Why a decision was refused before anything was written (issue #1105).</summary>
public enum NayaxSaleOwnerDecisionRefusal
{
    /// <summary>The decision does not apply to a sale in this state.</summary>
    NotAllowedForState = 10,

    /// <summary>The decision cannot rest on this kind of evidence.</summary>
    NotAllowedForEvidence = 20,

    /// <summary>The decision needs a reason and none was given.</summary>
    ReasonRequired = 30,

    /// <summary>The reason is longer than <see cref="NayaxSaleOwnerDecisionPolicy.MaxReasonLength"/>.</summary>
    ReasonTooLong = 40,
}

/// <summary>
/// The answer to "may this decision be applied?": the state the sale moves to, or the refusal.
/// </summary>
public sealed record NayaxSaleOwnerDecisionOutcome(
    NayaxSqsVerification? ResultingState, NayaxSaleOwnerDecisionRefusal? Refusal)
{
    public bool Allowed => Refusal is null;

    public static NayaxSaleOwnerDecisionOutcome Allow(NayaxSqsVerification state) => new(state, null);

    public static NayaxSaleOwnerDecisionOutcome Refuse(NayaxSaleOwnerDecisionRefusal refusal) => new(null, refusal);
}

/// <summary>
/// The decision table of issue #1105, and nothing else: which decision a business Owner may make
/// about a sale in which state, resting on which kind of evidence, and what the sale becomes.
///
/// <list type="table">
/// <item><term>Conflict + Use Nayax's values (comparison)</term><description>OwnerCorrected</description></item>
/// <item><term>Conflict + Keep the SQS values (comparison, reason)</term><description>OwnerConfirmed</description></item>
/// <item><term>Conflict or NeedsInvestigation + Don't count this sale (any evidence, reason)</term><description>OwnerExcluded</description></item>
/// <item><term>NeedsInvestigation + This sale is genuine (any evidence, reason)</term><description>OwnerConfirmed</description></item>
/// </list>
///
/// <para>Everything else is refused. "This sale is genuine" is never allowed on a conflict, because
/// a contradiction must be resolved by choosing which values are true; and neither conflict
/// resolution may rest on an <see cref="NayaxSaleProviderEvidenceKind.Absence"/>, because an absence
/// has no provider values to choose. Whether the evidence named is the sale's <em>latest</em>, and
/// whether the state is still the one presented, is the write's own check inside its write lock.</para>
/// </summary>
public static class NayaxSaleOwnerDecisionPolicy
{
    /// <summary>The longest reason accepted, in characters after trimming.</summary>
    public const int MaxReasonLength = 500;

    public static NayaxSaleOwnerDecisionOutcome Evaluate(
        NayaxSqsVerification? state,
        NayaxSaleOwnerDecision decision,
        NayaxSaleProviderEvidenceKind evidenceKind,
        string? reason)
    {
        var allowedHere = decision switch
        {
            NayaxSaleOwnerDecision.UseNayaxValues or NayaxSaleOwnerDecision.KeepSqsValues =>
                state is NayaxSqsVerification.Conflict,
            NayaxSaleOwnerDecision.DoNotCount =>
                state is NayaxSqsVerification.Conflict or NayaxSqsVerification.NeedsInvestigation,
            NayaxSaleOwnerDecision.Genuine =>
                state is NayaxSqsVerification.NeedsInvestigation,
            _ => false,
        };

        if (!allowedHere)
        {
            return NayaxSaleOwnerDecisionOutcome.Refuse(NayaxSaleOwnerDecisionRefusal.NotAllowedForState);
        }

        if (decision is NayaxSaleOwnerDecision.UseNayaxValues or NayaxSaleOwnerDecision.KeepSqsValues
            && evidenceKind is not NayaxSaleProviderEvidenceKind.Comparison)
        {
            return NayaxSaleOwnerDecisionOutcome.Refuse(NayaxSaleOwnerDecisionRefusal.NotAllowedForEvidence);
        }

        var trimmed = reason?.Trim();
        if (RequiresReason(decision) && string.IsNullOrEmpty(trimmed))
        {
            return NayaxSaleOwnerDecisionOutcome.Refuse(NayaxSaleOwnerDecisionRefusal.ReasonRequired);
        }

        if (trimmed is { Length: > MaxReasonLength })
        {
            return NayaxSaleOwnerDecisionOutcome.Refuse(NayaxSaleOwnerDecisionRefusal.ReasonTooLong);
        }

        return NayaxSaleOwnerDecisionOutcome.Allow(decision switch
        {
            NayaxSaleOwnerDecision.UseNayaxValues => NayaxSqsVerification.OwnerCorrected,
            NayaxSaleOwnerDecision.DoNotCount => NayaxSqsVerification.OwnerExcluded,
            _ => NayaxSqsVerification.OwnerConfirmed,
        });
    }

    /// <summary>Whether <paramref name="decision"/> needs a typed reason.</summary>
    public static bool RequiresReason(NayaxSaleOwnerDecision decision) =>
        decision is not NayaxSaleOwnerDecision.UseNayaxValues;

    /// <summary>
    /// The decisions the review page may offer for a sale in <paramref name="state"/> whose latest
    /// evidence is of <paramref name="evidenceKind"/>, in a fixed order.
    /// </summary>
    public static IReadOnlyList<NayaxSaleOwnerDecision> Available(
        NayaxSqsVerification? state, NayaxSaleProviderEvidenceKind? evidenceKind)
    {
        if (evidenceKind is not { } kind)
        {
            return [];
        }

        NayaxSaleOwnerDecision[] all =
        [
            NayaxSaleOwnerDecision.UseNayaxValues,
            NayaxSaleOwnerDecision.KeepSqsValues,
            NayaxSaleOwnerDecision.DoNotCount,
            NayaxSaleOwnerDecision.Genuine,
        ];

        // A placeholder reason, because a reason is the caller's to type; only state and evidence
        // decide what is offered.
        return all.Where(decision => Evaluate(state, decision, kind, "offered").Allowed).ToList();
    }
}
