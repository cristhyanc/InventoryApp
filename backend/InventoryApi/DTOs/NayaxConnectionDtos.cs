using Inventory.Application.Nayax;
using Inventory.Domain.Nayax;

namespace InventoryApi.DTOs;

/// <summary>
/// The credentials <c>POST /api/settings/nayax-connection/test</c> tests (issue #536).
///
/// These two fields are the whole request. It carries no business id, no actor, no draft id and no
/// result: the owner is resolved from the caller's membership, and everything the response reports
/// is the server's own finding. Both are nullable so a missing field is refused by the use case with
/// a caller-safe message rather than bound to an empty string and sent to Nayax as a blank
/// credential.
/// </summary>
/// <param name="OperatorId">The Nayax operator id to test. Not a secret.</param>
/// <param name="Token">
/// The Lynx API access token to test. A secret: it is sent to Nayax, stored only as ciphertext, and
/// never returned by this endpoint or any other.
/// </param>
public sealed record NayaxConnectionTestRequest(string? OperatorId, string? Token);

/// <summary>
/// The completed test <c>POST /api/settings/nayax-connection/save</c> accepts (issue #537).
///
/// One opaque identifier is the whole request, and that is the point: the credentials being stored
/// are exactly the ones the server tested, so a successful test cannot be presented as evidence for
/// a token nothing has tried. <strong>This contract must never gain an operator id, a token, a
/// status, a check result or a business id.</strong> Nullable so a missing field is refused with a
/// caller-safe message rather than bound to <see cref="Guid.Empty"/> and looked up.
/// </summary>
/// <param name="DraftId">The opaque identifier a completed test returned.</param>
public sealed record NayaxConnectionSaveRequest(Guid? DraftId);

/// <summary>
/// The business's Nayax connection state, as <c>GET /api/settings/nayax-connection/status</c>
/// returns it and as save and re-test report it (issue #537).
///
/// It is two fields and nothing else. Every member of the business may read this - the banners of
/// #539 are shown to a Manager and an Operator too - so the operator id, the credential revision,
/// the last-tested instant and the check lines are all deliberately absent: they are integration
/// configuration only the Owner who manages it has any use for.
/// </summary>
/// <param name="Status">
/// The published <see cref="NayaxConnectionStatus"/> name: <c>NotConfigured</c>,
/// <c>PendingPermissions</c>, <c>Ready</c> or <c>NeedsAttention</c> - a name rather than a number a
/// client would have to map.
/// </param>
/// <param name="MachineChecksPending">
/// Whether the last completed check left the machine-scoped checks unverified, because the business
/// had no machine in Nayax to run them against. A re-test once machines exist is what resolves it.
/// </param>
public sealed record NayaxConnectionStatusResponse(string Status, bool MachineChecksPending)
{
    /// <summary>Projects the use cases' summary, naming the status at this one boundary.</summary>
    public static NayaxConnectionStatusResponse From(NayaxConnectionStatusSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        return new NayaxConnectionStatusResponse(
            summary.Status.ToString(),
            summary.MachineChecksPending);
    }
}

/// <summary>One check line of the test result (issue #536).</summary>
/// <param name="Check">
/// The published <see cref="NayaxConnectionCheck"/> name - <c>SignIn</c>, <c>Machines</c>,
/// <c>Machine</c>, <c>ProductCatalogue</c>, <c>ProductGroups</c>, <c>MachineProducts</c>,
/// <c>MachineLastSales</c> or <c>MachineLastAlerts</c> - rather than a number a client would have to
/// map and a renumbering would silently change.
/// </param>
/// <param name="Status">
/// The published <see cref="NayaxConnectionCheckStatus"/> name: <c>Passed</c>, <c>Failed</c> or
/// <c>NotChecked</c>.
/// </param>
/// <param name="Failure">
/// The published <see cref="NayaxConnectionCheckFailure"/> name when the line failed:
/// <c>InvalidOrExpiredToken</c>, <c>PermissionNotGranted</c>, <c>OperatorIdMismatch</c>,
/// <c>NayaxUnavailable</c> or <c>UnexpectedResponse</c>; <see langword="null"/> otherwise.
/// Serialized as an explicit <c>null</c> rather than omitted, so the field is a stable part of the
/// shape.
/// </param>
public sealed record NayaxConnectionCheckResponse(string Check, string Status, string? Failure);

/// <summary>
/// What one connection test found, as <c>POST /api/settings/nayax-connection/test</c> returns it
/// (issue #536).
///
/// It deliberately does not echo the submitted token, or the operator id: the token is a secret the
/// server now holds encrypted, and a response that repeated either would put it into a browser
/// cache, a proxy log and whatever else sits between here and the Owner's screen. What the caller
/// gets back is the opaque draft id, when that draft stops existing, and every check line.
/// </summary>
/// <param name="DraftId">
/// The opaque identifier the tested credentials are held under, for the separate save step (#537).
/// It is a random GUID and carries nothing about the business, the actor or the credentials.
/// </param>
/// <param name="ExpiresAtUtc">When the draft stops existing, as a UTC instant.</param>
/// <param name="MachineCount">
/// How many machines the machine list returned, or <see langword="null"/> when that check did not
/// succeed. Zero is what makes the machine lines <c>NotChecked</c>.
/// </param>
/// <param name="NothingFailed">
/// Whether no check failed. <see langword="true"/> while the only non-passing lines were never
/// checked, so a business with no machines yet can still finish connecting.
/// </param>
/// <param name="Checks">Every declared check, in the server's own fixed order.</param>
public sealed record NayaxConnectionTestResponse(
    Guid DraftId,
    DateTime ExpiresAtUtc,
    int? MachineCount,
    bool NothingFailed,
    IReadOnlyList<NayaxConnectionCheckResponse> Checks)
{
    /// <summary>
    /// Projects the use case's result. Enum values become their declared names here, at the HTTP
    /// boundary, so the published contract is spelled in one place.
    /// </summary>
    public static NayaxConnectionTestResponse From(NayaxConnectionTestResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new NayaxConnectionTestResponse(
            result.DraftId,
            result.ExpiresAtUtc,
            result.Report.MachineCount,
            result.Report.NothingFailed,
            [.. result.Report.Lines.Select(line => new NayaxConnectionCheckResponse(
                line.Check.ToString(),
                line.Status.ToString(),
                line.Failure?.ToString()))]);
    }
}

/// <summary>
/// What one re-test of the stored credentials found, as
/// <c>POST /api/settings/nayax-connection/retest</c> returns it (issue #537).
///
/// It carries no draft id, because a re-test holds nothing back to be saved: it tests the token the
/// business is already using and writes what it concluded straight to the connection. It carries no
/// operator id and no token either, for the reasons the test response does not.
/// </summary>
/// <param name="Connection">The connection's state after the re-test.</param>
/// <param name="StatusUpdated">
/// Whether the re-test concluded anything and wrote it. <see langword="false"/> means Nayax never
/// answered the sign-in - a <c>5xx</c>, a timeout - so the stored status is untouched and the caller
/// should try again. The <c>SignIn</c> line's <c>NayaxUnavailable</c> cause says which.
/// </param>
/// <param name="MachineCount">
/// How many machines the machine list returned, or <see langword="null"/> when that check did not
/// succeed.
/// </param>
/// <param name="NothingFailed">Whether no check failed; unchecked machine lines are not failures.</param>
/// <param name="Checks">Every declared check, in the server's own fixed order.</param>
public sealed record NayaxConnectionRetestResponse(
    NayaxConnectionStatusResponse Connection,
    bool StatusUpdated,
    int? MachineCount,
    bool NothingFailed,
    IReadOnlyList<NayaxConnectionCheckResponse> Checks)
{
    /// <summary>Projects the use case's result.</summary>
    public static NayaxConnectionRetestResponse From(NayaxConnectionRetestResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new NayaxConnectionRetestResponse(
            NayaxConnectionStatusResponse.From(result.Summary),
            result.StatusUpdated,
            result.Report.MachineCount,
            result.Report.NothingFailed,
            [.. result.Report.Lines.Select(line => new NayaxConnectionCheckResponse(
                line.Check.ToString(),
                line.Status.ToString(),
                line.Failure?.ToString()))]);
    }
}
