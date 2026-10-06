using Inventory.Application.PlatformDiagnostics;
using InventoryApi.Auth.PlatformAdmin;
using InventoryApi.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

/// <summary>
/// The bounded, read-only, audited cross-business diagnostics API for the configured platform
/// super-administrator (issue #336).
///
/// <para><strong>Authorisation is server-side and on every request.</strong>
/// <c>[Authorize(Policy = PlatformAdminPolicy.Name)]</c> is evaluated by the authorization
/// middleware before any action here runs, and <c>BusinessScopeMiddleware</c> re-evaluates the same
/// policy before it allows the request past the membership requirement. There is no token claim, no
/// header, no route value and no body field that selects the authority: it is the configured Entra
/// <c>(tid, oid)</c> pair and nothing else.</para>
///
/// <para><strong>Read-only, and not a repair tool.</strong> This controller exposes exactly two
/// reads. Any future data repair must be a separately reviewed, named maintenance operation with a
/// preview/dry-run step and explicit verification - the shape <c>bootstrap-business</c>,
/// <c>migrate-documents</c> and <c>InventoryCostRepair</c> already use - and implementing one is
/// explicitly out of scope for issue #336. Nothing here is an execution path for one: the data path
/// cannot write, and a repair must never be reachable by submitting SQL.</para>
///
/// <para>The controller stays thin as the architecture rules require: it binds the statement, calls
/// the use case, and maps the outcome to a status code. It injects no <c>AppDbContext</c>, holds no
/// SQL and knows nothing about the permitted data surface or how it is enforced.</para>
/// </summary>
[ApiController]
[Route("api/admin/diagnostics")]
[Authorize(Policy = PlatformAdminPolicy.Name)]
[RequiredScope("access_as_user")]
[PlatformDiagnosticsEndpoint]
public sealed class AdminDiagnosticsController : ControllerBase
{
    private readonly RunDiagnosticsQuery _runQuery;
    private readonly PlatformDiagnosticsQueryLimits _limits;

    public AdminDiagnosticsController(RunDiagnosticsQuery runQuery, PlatformDiagnosticsQueryLimits limits)
    {
        _runQuery = runQuery;
        _limits = limits;
    }

    /// <summary>
    /// The capability signal for the UI in issue #335. Reaching it means the policy succeeded; the
    /// body carries the limits and nothing about any business.
    /// </summary>
    [HttpGet("access")]
    public ActionResult<PlatformDiagnosticsAccessResponse> Access() =>
        Ok(new PlatformDiagnosticsAccessResponse(
            Authorized: true,
            CrossBusinessScope: true,
            new PlatformDiagnosticsLimitsResponse(
                PlatformDiagnosticsQueryLimits.MaxSqlBytes,
                _limits.MaxRows,
                _limits.MaxResponseBytes,
                _limits.MaxDuration.TotalSeconds)));

    /// <summary>
    /// Runs one statement against the permitted diagnostics surface and returns a bounded result.
    /// </summary>
    [HttpPost("query")]
    public async Task<ActionResult<PlatformDiagnosticsQueryResponse>> Query(
        PlatformDiagnosticsQueryRequest? request,
        CancellationToken cancellationToken)
    {
        var result = await _runQuery.Handle(request?.Sql, cancellationToken);
        var response = ToResponse(result);

        // A refusal and a provider error are both answers about the statement the caller authored,
        // so they are 400s rather than 500s; a timeout is 408 because the request did not complete
        // in the time the server allows. The body carries the specific outcome either way, so the
        // status code never has to stand in for it.
        return result.Outcome switch
        {
            DiagnosticsQueryOutcome.Succeeded or DiagnosticsQueryOutcome.Truncated => Ok(response),
            DiagnosticsQueryOutcome.TimedOut or DiagnosticsQueryOutcome.Cancelled =>
                StatusCode(StatusCodes.Status408RequestTimeout, response),
            _ => BadRequest(response),
        };
    }

    private static PlatformDiagnosticsQueryResponse ToResponse(DiagnosticsQueryResult result) =>
        new(
            result.Outcome.ToString(),
            result.DenialReason == DiagnosticsQueryDenialReason.None ? null : result.DenialReason.ToString(),
            result.Message,
            result.CrossBusinessScope,
            result.Columns,
            result.Rows,
            result.RowCount,
            result.Truncated,
            result.TruncationReason?.ToString(),
            result.DurationMilliseconds,
            result.QueryFingerprint);
}
