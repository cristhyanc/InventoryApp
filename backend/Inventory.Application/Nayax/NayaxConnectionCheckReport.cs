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
}
