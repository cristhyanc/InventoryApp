using System.Diagnostics;
using Inventory.Application.Access;
using Inventory.Application.SupportSessions;
using InventoryApi.Auth.PlatformAdmin;
using Microsoft.AspNetCore.Mvc;

namespace InventoryApi.Auth;

/// <summary>
/// Refuses a role preview nobody may make, one nobody can read, and one that would change something
/// (issue #720). It is the only thing in this feature that refuses a request; everything else about
/// a preview is the narrowing <c>CurrentCapabilities</c> applies.
///
/// <para><strong>Why a middleware rather than the capability handler.</strong> The read-only rule
/// has to hold on <em>every</em> endpoint - the capability ones, the three that need no membership,
/// and the Super Admin's own - and the capability authorization handler only runs where a capability
/// is declared. A rule added per endpoint class is a rule somebody forgets to add to the next class,
/// which is the same reasoning that puts the support session's persistence refusal in
/// <c>BusinessOwnershipEnforcer</c> rather than in each controller.</para>
///
/// <para><strong>Where it sits, and why.</strong> After <c>UseAuthentication()</c>, because whether
/// a preview is honoured at all is the platform-admin policy's answer about this caller; and before
/// <c>UseAuthorization()</c> and <c>BusinessScopeMiddleware</c>, so a refusal happens before the
/// capability policies, before any business is resolved, before the endpoint runs and before issue
/// #524's profile refresh is ever scheduled. A previewed request that is refused here therefore
/// writes nothing anywhere, which is the whole point of the rule.</para>
///
/// <para><strong>The precedence between the refusals is deliberate</strong> and is the order of how
/// little of the request is usable: <c>role_preview_not_permitted</c> first - the caller may not ask
/// at all, so nothing else needs deciding - then <c>invalid_role_preview</c> - the preview cannot be
/// read, so it cannot be applied, and a request that cannot be narrowed must not be answered
/// unnarrowed - then a support session's own refusals, then <c>read_only_role_preview</c>, the one
/// refusal that says the preview itself was fine.</para>
///
/// <para>The support session's two refusals come first because they are about a credential rather
/// than a view: a dead session and a write inside a live one are the same refusals whether a preview
/// was asked for or not, and a client has to be told that its session ended rather than that its
/// preview was read-only. Those refusals are made where they already were - the capability
/// authorization handler and <see cref="BusinessScopeMiddleware"/> - so this middleware steps aside
/// for exactly the requests they answer: a request that presented a session, on an endpoint where a
/// session is honoured. On an endpoint that bypasses the membership requirement the
/// <c>X-Support-Session</c> header is not acted on at all, so nothing downstream would refuse the
/// write and this middleware refuses it here.</para>
///
/// <para><strong>An unauthenticated request is left alone</strong>, so it keeps the <c>401</c> the
/// authentication and authorization middleware answer it with, and the anonymous health probes are
/// unaffected. See <see cref="RequestRolePreviewAccessor"/> for why.</para>
/// </summary>
public sealed class RolePreviewMiddleware
{
    /// <summary>
    /// What a caller who may not preview a role is told. It names the header and the rule and
    /// nothing about who may use it, which business exists, or who the caller is.
    /// </summary>
    internal const string NotPermittedMessage =
        "This request may not preview a role.";

    /// <summary>
    /// What a caller whose preview could not be read is told. It names the three accepted values,
    /// because unlike the other two refusals this one is a request the caller can correct.
    /// </summary>
    internal const string InvalidMessage =
        "The X-Role-Preview header must be exactly one of Owner, Manager or Operator, sent once.";

    /// <summary>What a previewed request that would have changed something is told.</summary>
    internal const string ReadOnlyMessage =
        "A role preview is read-only, so this request cannot change anything.";

    private readonly RequestDelegate _next;
    private readonly ILogger<RolePreviewMiddleware> _logger;

    public RolePreviewMiddleware(RequestDelegate next, ILogger<RolePreviewMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(
        HttpContext context,
        IRolePreviewAccessor rolePreviewAccessor,
        ISupportSessionAccessor supportSessionAccessor)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(rolePreviewAccessor);
        ArgumentNullException.ThrowIfNull(supportSessionAccessor);

        // Every request that is not previewing anything leaves on one dictionary lookup, which is
        // every request today's application makes.
        if (!context.Request.Headers.ContainsKey(RolePreviewHeader.Name))
        {
            await _next(context);
            return;
        }

        var preview = await rolePreviewAccessor.ResolveAsync(context.RequestAborted);

        if (preview.State == RolePreviewState.NotPermitted)
        {
            await RefuseAsync(
                context,
                StatusCodes.Status403Forbidden,
                RolePreviewErrorCodes.NotPermitted,
                NotPermittedMessage);
            return;
        }

        if (preview.State == RolePreviewState.Invalid)
        {
            await RefuseAsync(
                context,
                StatusCodes.Status400BadRequest,
                RolePreviewErrorCodes.Invalid,
                InvalidMessage);
            return;
        }

        if (preview.State == RolePreviewState.NotRequested)
        {
            // The header is there but was not read, which today means the request is not
            // authenticated. It keeps its ordinary answer; see the class remarks.
            await _next(context);
            return;
        }

        if (RolePreviewPolicy.IsReadMethod(context.Request.Method))
        {
            await _next(context);
            return;
        }

        if (await SupportSessionRefusesThisRequestAsync(context, supportSessionAccessor))
        {
            await _next(context);
            return;
        }

        await RefuseAsync(
            context,
            StatusCodes.Status403Forbidden,
            RolePreviewErrorCodes.ReadOnly,
            ReadOnlyMessage);
    }

    /// <summary>
    /// Whether this non-read request is one the support session's own read-only rule already
    /// refuses, in which case that refusal - and its own code - is the answer the caller gets.
    ///
    /// <para>It is true only for a request that presented a session <em>and</em> reached an endpoint
    /// where a session is honoured. The two endpoint families that bypass the membership requirement
    /// never act on the <c>X-Support-Session</c> header at all - the Super Admin's own control
    /// surface is reached as the administrator themselves - so for them nothing downstream would
    /// refuse a write and the preview's own rule is what has to.</para>
    /// </summary>
    private static async Task<bool> SupportSessionRefusesThisRequestAsync(
        HttpContext context,
        ISupportSessionAccessor supportSessionAccessor)
    {
        var endpoint = context.GetEndpoint();

        if (endpoint?.Metadata.GetMetadata<PlatformDiagnosticsEndpointAttribute>() is not null
            || endpoint?.Metadata.GetMetadata<MembershipNotRequiredEndpointAttribute>() is not null)
        {
            return false;
        }

        var support = await supportSessionAccessor.ResolveAsync(context.RequestAborted);

        return support.State != SupportSessionAccessState.NotRequested;
    }

    /// <summary>
    /// Refuses the request with the stable <paramref name="code"/> and nothing else: the body is RFC
    /// 7807 with the same <c>code</c>/<c>message</c> extensions the application's other handled
    /// refusals carry, and it names no business, no actor, no capability and not the value the
    /// header carried.
    /// </summary>
    private async Task RefuseAsync(HttpContext context, int status, string code, string detail)
    {
        _logger.LogWarning(
            "A role-preview request was refused. RolePreviewErrorCode={RolePreviewErrorCode} "
                + "Method={RequestMethod}.",
            code,
            context.Request.Method);

        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";

        var problemDetails = new ProblemDetails
        {
            Type = status == StatusCodes.Status400BadRequest
                ? "https://tools.ietf.org/html/rfc9110#section-15.5.1"
                : "https://tools.ietf.org/html/rfc9110#section-15.5.4",
            Title = status == StatusCodes.Status400BadRequest ? "Bad Request" : "Forbidden",
            Detail = detail,
            Status = status,
        };
        problemDetails.Extensions["traceId"] = Activity.Current?.Id ?? context.TraceIdentifier;
        // Kept beside the standard `detail` for the reason DomainExceptionHandler keeps it: the
        // existing frontend error handling reads `error.message`.
        problemDetails.Extensions["message"] = detail;
        problemDetails.Extensions["code"] = code;

        await context.Response.WriteAsJsonAsync(problemDetails, context.RequestAborted);
    }
}
