namespace Inventory.Domain.NayaxSqs;

/// <summary>
/// What one machine's <c>lastSales</c> response did return, as the absence rule reads it (issue
/// #1104). It is recorded on the evidence row exactly as observed, so a person reading the row later
/// can see what the question was asked against.
///
/// <para>It is deliberately three facts and <strong>no completeness claim</strong>: the number of
/// items and the two instants at the ends of them. Nayax documents <c>lastSales</c> as "the most
/// recent sales" for a machine - not how many, and not a window - so "the provider returned items
/// between these two instants" is all that is knowable and all that is stored.</para>
/// </summary>
/// <param name="MachineId">The machine whose <c>lastSales</c> was read.</param>
/// <param name="ReturnedItemCount">How many items the response carried for it.</param>
/// <param name="EarliestReturnedInstantUtc">
/// The earliest authorization instant among them, or <see langword="null"/> when none carried one.
/// </param>
/// <param name="LatestReturnedInstantUtc">The latest, on the same terms.</param>
public sealed record NayaxProviderMachineWindow(
    long MachineId,
    int ReturnedItemCount,
    DateTime? EarliestReturnedInstantUtc,
    DateTime? LatestReturnedInstantUtc);

/// <summary>
/// What was observed about one stored sale that <c>lastSales</c> did not return (issue #1104).
///
/// <para><strong>There is no completeness property, on purpose.</strong> Every field here is
/// something that was seen, and none of them says or implies that the provider's response was
/// complete for any range. That is the structural half of the issue's rule that absence from
/// <c>lastSales</c> is never proof: there is nothing for a later reader to misread as one.</para>
/// </summary>
/// <param name="Bracketed">
/// Whether the sale's own authorization instant lies <em>strictly</em> between the earliest and the
/// latest instants the response returned for its machine.
/// </param>
/// <param name="OutsideReceiptGrace">
/// Whether the sale had been in this application's hands for longer than
/// <see cref="NayaxSaleProviderAbsencePolicy.ReceiptGrace"/> when the run started.
/// </param>
/// <param name="RecordsEvidence">
/// Whether this observation is written down at all. Only a bracketed sale outside the grace is:
/// anything else is the rolling window being a rolling window, and recording it would be noise that
/// looks like a finding.
/// </param>
/// <param name="ObservedState">
/// The state this observation is evidence for - <see cref="NayaxSqsVerification.NeedsInvestigation"/>
/// when it records evidence, and <see langword="null"/> otherwise, which leaves the sale exactly as
/// it was.
/// </param>
public sealed record NayaxSaleAbsenceObservation(
    bool Bracketed,
    bool OutsideReceiptGrace,
    bool RecordsEvidence,
    NayaxSqsVerification? ObservedState);

/// <summary>
/// "The provider's own record of this machine did not include this sale - what does that tell us?"
/// (issue #1104, child of #1100).
///
/// <para><strong>The answer is never "the sale did not happen".</strong> Nayax documents
/// <c>GET /v1/machines/{MachineID}/lastSales</c> as "the most recent sales transactions for a
/// specific machine"
/// (https://devzone.nayax.com/reference/lynx/machines/get-last-sales-for-machine-by-machineid): the
/// contract states neither how many items it returns nor any authorization-time window it covers.
/// A transaction missing from it is therefore not evidence that the transaction did not happen, and
/// a stored sale is never treated as proven fake - by this policy or by anything reading what it
/// records.</para>
///
/// <para><strong>Two conditions make an absence worth a person's time, and both must hold.</strong>
/// The sale's own instant has to lie <em>strictly inside</em> the range the response did return
/// (bracketed), because that is the only part of the response the provider demonstrably reached; and
/// the sale has to have been in hand for longer than <see cref="ReceiptGrace"/>, because a
/// transaction the queue handed over moments ago may simply not be in the provider's own record
/// yet. Either one failing means nothing is recorded and the sale keeps its state - which is also
/// what keeps the evidence table from filling with rows about a rolling window rolling.</para>
///
/// <para>It is a pure function over instants: no clock, no database, no configuration, and the two
/// constants are the repository owner's values of 5 November 2026 rather than settings.</para>
/// </summary>
public static class NayaxSaleProviderAbsencePolicy
{
    /// <summary>
    /// How long a sale must have been in this application's hands before its absence from
    /// <c>lastSales</c> counts as an observation worth recording: ten minutes.
    ///
    /// <para>It is measured from the sale's own <c>ReceivedViaSqsAtUtc</c> to the instant the run
    /// started, not to the instant each machine was read: the run start is one instant for the whole
    /// run, so which machine a sale happened to be on cannot change whether it was inside the
    /// grace.</para>
    /// </summary>
    public static readonly TimeSpan ReceiptGrace = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Observes one stored sale that <paramref name="window"/>'s response did not return.
    /// </summary>
    /// <param name="window">What the response did return for the sale's machine.</param>
    /// <param name="saleAuthorizationInstantUtc">
    /// The sale's own stored authorization instant, or <see langword="null"/> when it holds none - in
    /// which case it cannot be placed inside any range and nothing is recorded.
    /// </param>
    /// <param name="saleReceivedViaSqsAtUtc">
    /// When the queue reader accepted the message that stored the sale, or <see langword="null"/> for
    /// a sale that did not arrive from the queue - in which case there is no receipt for the grace to
    /// be measured from and nothing is recorded.
    /// </param>
    /// <param name="runStartedAtUtc">When this reconciliation run began reading.</param>
    public static NayaxSaleAbsenceObservation Observe(
        NayaxProviderMachineWindow window,
        DateTime? saleAuthorizationInstantUtc,
        DateTime? saleReceivedViaSqsAtUtc,
        DateTime runStartedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(window);

        var bracketed = IsBracketed(window, saleAuthorizationInstantUtc);
        var outsideGrace = saleReceivedViaSqsAtUtc is { } receivedAtUtc
            && runStartedAtUtc - receivedAtUtc > ReceiptGrace;

        var records = bracketed && outsideGrace;

        return new NayaxSaleAbsenceObservation(
            Bracketed: bracketed,
            OutsideReceiptGrace: outsideGrace,
            RecordsEvidence: records,
            ObservedState: records ? NayaxSqsVerification.NeedsInvestigation : null);
    }

    /// <summary>
    /// Strictly between the two ends, which is what makes the bound defensible. A sale at either
    /// boundary is one of the items the window reached rather than something inside it, and an empty
    /// or single-item response has no interior at all - so neither brackets anything.
    /// </summary>
    private static bool IsBracketed(
        NayaxProviderMachineWindow window, DateTime? saleAuthorizationInstantUtc) =>
        saleAuthorizationInstantUtc is { } instant
        && window.EarliestReturnedInstantUtc is { } earliest
        && window.LatestReturnedInstantUtc is { } latest
        && instant > earliest
        && instant < latest;
}
