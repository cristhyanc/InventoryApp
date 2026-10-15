using System.Text.Json.Serialization;
using Inventory.Domain.Nayax;

namespace Inventory.Application.Nayax;

/// <summary>
/// One line of the Nayax connection test result (issue #536): one check, how it turned out, and -
/// when it failed - why.
///
/// <paramref name="Failure"/> is set if and only if <paramref name="Status"/> is
/// <see cref="NayaxConnectionCheckStatus.Failed"/>. A passed or unchecked line carries no cause,
/// and a failed one always carries one, so a client never has to invent a reason for a cross it is
/// showing.
/// </summary>
/// <param name="Check">The read-only Nayax call this line reports.</param>
/// <param name="Status">Whether it passed, failed, or was never made.</param>
/// <param name="Failure">Why it failed, or <see langword="null"/> when it did not.</param>
public sealed record NayaxConnectionCheckLine(
    NayaxConnectionCheck Check,
    NayaxConnectionCheckStatus Status,
    NayaxConnectionCheckFailure? Failure)
{
    /// <summary>A line for a check that answered successfully.</summary>
    public static NayaxConnectionCheckLine Passed(NayaxConnectionCheck check) =>
        new(check, NayaxConnectionCheckStatus.Passed, null);

    /// <summary>A line for a check Nayax refused or could not answer.</summary>
    public static NayaxConnectionCheckLine Failed(
        NayaxConnectionCheck check,
        NayaxConnectionCheckFailure failure) =>
        new(check, NayaxConnectionCheckStatus.Failed, failure);

    /// <summary>A line for a check that was never made, so nothing is known about it.</summary>
    public static NayaxConnectionCheckLine NotChecked(NayaxConnectionCheck check) =>
        new(check, NayaxConnectionCheckStatus.NotChecked, null);
}

/// <summary>
/// The complete result of one read-only Nayax connection test (issue #536).
///
/// It carries no token, no ciphertext and no Nayax response body - only which checks were made and
/// how each one answered - so it is safe to return to the Owner who ran it and safe to store beside
/// the draft. <see cref="MachineCount"/> is the one fact read from Nayax that is reported, because
/// "no machines" is why the machine checks read
/// <see cref="NayaxConnectionCheckStatus.NotChecked"/> and an operator otherwise has no way to tell
/// that apart from a check that was skipped for some other reason.
/// </summary>
/// <param name="Lines">
/// Every declared check, in <see cref="NayaxConnectionCheckPolicy.All"/> order. The list is complete:
/// a check that could not run is present as <see cref="NayaxConnectionCheckStatus.NotChecked"/>
/// rather than missing.
/// </param>
/// <param name="MachineCount">
/// How many machines the machine list returned, or <see langword="null"/> when that check itself did
/// not succeed and the count is therefore unknown.
/// </param>
public sealed record NayaxConnectionCheckReport(
    IReadOnlyList<NayaxConnectionCheckLine> Lines,
    int? MachineCount)
{
    /// <summary>
    /// Whether every check that ran passed. Deliberately false while any line failed and
    /// <em>true</em> while the only non-passing lines were never checked: an unchecked machine line
    /// is not a failure of the credentials, and a business with no machines yet must still be able
    /// to finish connecting. A client that needs the distinction reads <see cref="Lines"/>, which
    /// keeps it.
    /// </summary>
    public bool NothingFailed => Lines.All(line => line.Status != NayaxConnectionCheckStatus.Failed);

    /// <summary>
    /// Whether this run left the machine-scoped checks unverified because the business had no
    /// machine in Nayax to run them against (issue #537): the machine list was read, and a
    /// machine-scoped line was still never checked.
    ///
    /// It is what a saved or re-tested connection records so the wizard can ask for a re-test once
    /// machines exist, and it is deliberately not a failure: the credentials did nothing wrong, and
    /// <see cref="NothingFailed"/> stays <see langword="true"/>. Which checks need a machine is the
    /// Domain's answer, not a list repeated here.
    ///
    /// A machine list that was itself refused or never checked does not set it (issue #681): those
    /// machine lines are unchecked for want of the list, not of a machine, and "set up your machines"
    /// would send an Owner who has them to the wrong fix. That run's own failed line is what the
    /// stored status and the checklist report instead.
    /// </summary>
    [JsonIgnore]
    public bool MachineChecksPending =>
        Lines.Any(line =>
            line.Check == NayaxConnectionCheck.Machines
            && line.Status == NayaxConnectionCheckStatus.Passed)
        && Lines.Any(line =>
            NayaxConnectionCheckPolicy.RequiresMachine(line.Check)
            && line.Status == NayaxConnectionCheckStatus.NotChecked);

    /// <summary>
    /// What this run concluded about the credentials it tested (issue #537), through the one Domain
    /// rule. The projection - which line is the sign-in, and whether any other line failed - is
    /// here; what each answer means is <see cref="NayaxConnectionCheckOutcomePolicy"/>'s.
    ///
    /// A report with no sign-in line at all reads as a sign-in that was never checked, and so
    /// concludes nothing. That cannot come from the runner, which reports every declared check, but
    /// it can come from a stored report, and the fail-closed answer is the same either way.
    /// </summary>
    [JsonIgnore]
    public NayaxConnectionCheckOutcome Outcome
    {
        get
        {
            var signIn = Lines.FirstOrDefault(line => line.Check == NayaxConnectionCheck.SignIn);

            return NayaxConnectionCheckOutcomePolicy.Classify(
                signIn?.Status ?? NayaxConnectionCheckStatus.NotChecked,
                signIn?.Failure,
                Lines.Any(line =>
                    line.Check != NayaxConnectionCheck.SignIn
                    && line.Status == NayaxConnectionCheckStatus.Failed));
        }
    }
}
