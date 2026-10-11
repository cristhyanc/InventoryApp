namespace Inventory.Domain.Nayax;

/// <summary>
/// One read-only Nayax Lynx call the connection test makes, as the agreed list of issue #536 (one
/// slice of #506 under #328) names it. This is the complete list: a check is never derived at
/// runtime and never comes from a request.
///
/// There is exactly one check per endpoint rather than one per feature, because the answer an
/// operator needs is per endpoint: a token that authenticates but lacks one permission is the
/// ordinary case this test exists for, and collapsing two endpoints into one line would hide which
/// of them was refused. Declaration order is the order the checks run in and the order they are
/// reported in, so the result is deterministic without a client having to sort.
///
/// Every check is a <c>GET</c>. The write call <c>POST machines/{id}/machineProducts</c> is
/// deliberately absent and must stay absent: testing a connection must never change an operator's
/// Nayax data.
/// </summary>
public enum NayaxConnectionCheck
{
    /// <summary>Signing in at all: <c>GET devices</c>.</summary>
    SignIn = 1,

    /// <summary>The machine list: <c>GET machines</c>. Also decides whether the machine checks run.</summary>
    Machines = 2,

    /// <summary>One machine's details: <c>GET machines/{id}</c>.</summary>
    Machine = 3,

    /// <summary>
    /// The operator product catalogue: <c>GET operators/{operatorId}/products</c>. Operator-scoped,
    /// so it also proves the submitted operator id belongs to the submitted token.
    /// </summary>
    ProductCatalogue = 4,

    /// <summary>
    /// The operator product groups: <c>GET operators/{operatorId}/productGroups</c>. Operator-scoped
    /// for the same reason as <see cref="ProductCatalogue"/>.
    /// </summary>
    ProductGroups = 5,

    /// <summary>One machine's product map: <c>GET machines/{id}/machineProducts</c>.</summary>
    MachineProducts = 6,

    /// <summary>One machine's rolling sales window: <c>GET machines/{id}/lastSales</c>.</summary>
    MachineLastSales = 7,

    /// <summary>One machine's last alerts: <c>GET machines/{id}/lastAlerts</c>.</summary>
    MachineLastAlerts = 8,
}

/// <summary>How one <see cref="NayaxConnectionCheck"/> turned out (issue #536).</summary>
public enum NayaxConnectionCheckStatus
{
    /// <summary>Nayax answered successfully: the token carries this permission.</summary>
    Passed = 1,

    /// <summary>Nayax refused, or could not be reached. See the check's failure cause.</summary>
    Failed = 2,

    /// <summary>
    /// The check was not made at all, so nothing is known about it. The only reason in this slice is
    /// a machine-scoped check with no machine to run it against - the business has none, or the
    /// machine list itself could not be read. It is deliberately not reported as a pass or a
    /// failure: an untested permission must never look like a granted one.
    /// </summary>
    NotChecked = 3,
}

/// <summary>
/// Why one <see cref="NayaxConnectionCheck"/> failed (issue #536), in the operator's language rather
/// than in HTTP's.
///
/// The three refusal causes are Nayax's own documented ones
/// (https://devzone.nayax.com/docs/manage-data-operations/lynx-api/app-tokens): a <c>401</c> is a
/// verdict on the token, a <c>403</c> is a verdict on one permission, and the operator-scoped
/// endpoints answer a submitted operator id the token does not cover rather than the resource.
/// </summary>
public enum NayaxConnectionCheckFailure
{
    /// <summary>Nayax answered <c>401</c>: the token is invalid or has expired.</summary>
    InvalidOrExpiredToken = 1,

    /// <summary>Nayax answered <c>403</c>: the token's scopes do not cover this endpoint.</summary>
    PermissionNotGranted = 2,

    /// <summary>
    /// An operator-scoped endpoint answered <c>404</c>: the submitted operator id is not one this
    /// token can read, so the pair does not belong together.
    /// </summary>
    OperatorIdMismatch = 3,

    /// <summary>Nayax answered <c>5xx</c>, timed out, or could not be reached at all.</summary>
    NayaxUnavailable = 4,

    /// <summary>
    /// Nayax answered something this application does not interpret - a <c>400</c>, a <c>429</c>, a
    /// <c>404</c> on an endpoint that is not operator-scoped, or a body that is not the documented
    /// shape. Reported as its own cause rather than folded into one of the four above: guessing
    /// which verdict an unrecognised answer means is exactly how a test tells an operator the wrong
    /// thing to fix.
    /// </summary>
    UnexpectedResponse = 5,
}
