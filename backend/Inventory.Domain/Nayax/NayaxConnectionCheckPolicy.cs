using System.Collections.Frozen;

namespace Inventory.Domain.Nayax;

/// <summary>
/// The deterministic rules of the Nayax connection test (issue #536): which checks there are, which
/// of them need a machine, which of them are operator-scoped, and what a Nayax answer means.
///
/// It lives in the Domain, beside <see cref="NayaxConnectionStatusGate"/>, for the same reason that
/// gate does: the meaning of a Nayax refusal is not a policy of whichever adapter happened to make
/// the call. The runner that makes the calls, the tests that pin the mapping and the wizard that
/// will eventually explain the result all read this one table, and a second opinion written inline
/// in an adapter is how "lacks a permission" quietly becomes "token expired" on somebody's screen.
///
/// It holds no HTTP type on purpose - <see cref="Classify"/> takes a status code as a number - so
/// the Domain keeps no dependency on <c>System.Net.Http</c> or on any client that might call Nayax.
/// </summary>
public static class NayaxConnectionCheckPolicy
{
    /// <summary>
    /// Every check, in declaration order, which is the order they run in and are reported in.
    /// </summary>
    public static IReadOnlyList<NayaxConnectionCheck> All { get; } =
        [.. Enum.GetValues<NayaxConnectionCheck>()];

    /// <summary>
    /// The checks whose endpoint carries the operator id in its path. Only these can answer "that
    /// operator is not yours", which is why only these turn a <c>404</c> into
    /// <see cref="NayaxConnectionCheckFailure.OperatorIdMismatch"/>.
    /// </summary>
    private static readonly FrozenSet<NayaxConnectionCheck> OperatorScoped = FrozenSet.ToFrozenSet(
    [
        NayaxConnectionCheck.ProductCatalogue,
        NayaxConnectionCheck.ProductGroups,
    ]);

    /// <summary>
    /// The checks that need a machine id in their path, so they can only run once the machine list
    /// has been read and has at least one machine in it.
    /// </summary>
    private static readonly FrozenSet<NayaxConnectionCheck> MachineScoped = FrozenSet.ToFrozenSet(
    [
        NayaxConnectionCheck.Machine,
        NayaxConnectionCheck.MachineProducts,
        NayaxConnectionCheck.MachineLastSales,
        NayaxConnectionCheck.MachineLastAlerts,
    ]);

    /// <summary>Whether <paramref name="check"/> calls an endpoint that carries the operator id.</summary>
    public static bool IsOperatorScoped(NayaxConnectionCheck check) => OperatorScoped.Contains(check);

    /// <summary>Whether <paramref name="check"/> needs a machine id, and so a machine to run against.</summary>
    public static bool RequiresMachine(NayaxConnectionCheck check) => MachineScoped.Contains(check);

    /// <summary>
    /// What one failed check's answer means.
    ///
    /// <paramref name="statusCode"/> is <see langword="null"/> when Nayax never answered at all - a
    /// timeout, a dropped connection, or a response this application could not read - which is
    /// reported as <see cref="NayaxConnectionCheckFailure.NayaxUnavailable"/> rather than as a
    /// verdict on the credentials, because nothing about them was learned.
    ///
    /// The mapping is an allow-list of the answers Nayax documents. Anything else, including a
    /// <c>404</c> on an endpoint that carries no operator id, is
    /// <see cref="NayaxConnectionCheckFailure.UnexpectedResponse"/>: an answer nobody has
    /// interpreted must not be presented as one that has been.
    /// </summary>
    /// <param name="check">The check that failed, which decides how a <c>404</c> reads.</param>
    /// <param name="statusCode">
    /// The HTTP status Nayax answered with, or <see langword="null"/> when it never answered.
    /// </param>
    public static NayaxConnectionCheckFailure Classify(NayaxConnectionCheck check, int? statusCode) =>
        statusCode switch
        {
            null => NayaxConnectionCheckFailure.NayaxUnavailable,
            401 => NayaxConnectionCheckFailure.InvalidOrExpiredToken,
            403 => NayaxConnectionCheckFailure.PermissionNotGranted,
            404 when IsOperatorScoped(check) => NayaxConnectionCheckFailure.OperatorIdMismatch,
            // Nayax answered, but about itself rather than about the credentials (issue #681). A
            // 408 is its own request timeout and a 429 is its own rate limit, so both say "ask me
            // again shortly" and neither is a verdict on the token, the operator id or a
            // permission - exactly what NayaxUnavailable means. Reported as "unrecognised" they
            // sent an Owner looking for a permission to request, which is the one thing that would
            // not help.
            408 or 429 => NayaxConnectionCheckFailure.NayaxUnavailable,
            >= 500 and <= 599 => NayaxConnectionCheckFailure.NayaxUnavailable,
            _ => NayaxConnectionCheckFailure.UnexpectedResponse,
        };
}
