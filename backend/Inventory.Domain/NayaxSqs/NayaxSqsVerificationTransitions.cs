namespace Inventory.Domain.NayaxSqs;

/// <summary>
/// Which moves a sale's <see cref="NayaxSqsVerification"/> may make, and on what (issue #1104).
///
/// <para><strong>It is an allowlist, deliberately.</strong> A fall-through that permitted whatever a
/// caller asked for would make "only these transitions happen" a claim about every caller rather than
/// a rule, and a value added to the enum later would inherit it silently. Here a pair that is not
/// listed is refused, and <see cref="Apply"/> answers the state the sale keeps.</para>
///
/// <para><strong>A contradiction is terminal and a null state is not a state.</strong>
/// <see cref="NayaxSqsVerification.Conflict"/> is left exactly as it is by every later observation -
/// the evidence is appended beside it, and only an Owner decision (issue #1105) resolves it - and a
/// sale that never arrived from the queue carries no value at all, so nothing this reconciliation
/// observes may give it one.</para>
/// </summary>
public static class NayaxSqsVerificationTransitions
{
    /// <summary>
    /// Whether provider evidence may move a sale from <paramref name="from"/> to
    /// <paramref name="to"/>.
    ///
    /// <para><see langword="null"/> for <paramref name="from"/> is "this sale did not arrive from the
    /// queue", which is never moved into the model. A pair whose two states are the same is refused
    /// too: re-recording the state a row already holds is not a transition, and keeping it out of
    /// here is what lets the evidence rule above ask "did this run change the state?" and get a
    /// truthful answer.</para>
    /// </summary>
    public static bool IsAllowed(NayaxSqsVerification? from, NayaxSqsVerification to) =>
        from switch
        {
            NayaxSqsVerification.Unverified =>
                to is NayaxSqsVerification.Verified
                    or NayaxSqsVerification.Conflict
                    or NayaxSqsVerification.NeedsInvestigation,

            // Later matching evidence clears a flag; a later contradiction overrides it.
            NayaxSqsVerification.NeedsInvestigation =>
                to is NayaxSqsVerification.Verified or NayaxSqsVerification.Conflict,

            // A verified sale is not immune to later contradicting evidence. The earlier evidence is
            // kept, so what it was verified by stays readable.
            NayaxSqsVerification.Verified =>
                to is NayaxSqsVerification.Conflict or NayaxSqsVerification.NeedsInvestigation,

            // Terminal: only an Owner decision (issue #1105) moves a row out of a contradiction.
            NayaxSqsVerification.Conflict => false,

            // An Owner decision (issue #1105) stands until later provider evidence contradicts the
            // decided values. Matching evidence changes nothing: an owner-decided row is never
            // promoted to Verified, because "Confirmed by owner" and "Nayax-verified" are different
            // facts. Which evidence may supersede a decision - a comparison that differs from the one
            // the decision relied on, never an absence - is decided by the evidence write, which is
            // the only place that can see both.
            NayaxSqsVerification.OwnerConfirmed
                or NayaxSqsVerification.OwnerCorrected
                or NayaxSqsVerification.OwnerExcluded =>
                to is NayaxSqsVerification.Conflict or NayaxSqsVerification.NeedsInvestigation,

            // Not a queue sale, or a state this build does not declare. Fail closed either way.
            _ => false,
        };

    /// <summary>
    /// The state the sale holds after this observation: <paramref name="observed"/> when the move is
    /// allowed, and <paramref name="current"/> unchanged otherwise.
    ///
    /// <para><paramref name="observed"/> is <see langword="null"/> when the observation decided no
    /// state - a comparison with no authorization instant on one side cannot verify anything and
    /// contradicts nothing - and the sale then keeps the state it had, with the evidence still
    /// recorded.</para>
    /// </summary>
    public static NayaxSqsVerification? Apply(
        NayaxSqsVerification? current, NayaxSqsVerification? observed) =>
        observed is { } next && IsAllowed(current, next) ? next : current;

    /// <summary>
    /// Whether <paramref name="state"/> records a business Owner's decision (issue #1105).
    /// </summary>
    public static bool IsOwnerDecided(NayaxSqsVerification? state) =>
        state is NayaxSqsVerification.OwnerConfirmed
            or NayaxSqsVerification.OwnerCorrected
            or NayaxSqsVerification.OwnerExcluded;
}
