using System.Diagnostics;
using Inventory.Application.SupportSessions;
using Microsoft.AspNetCore.Diagnostics;

namespace InventoryApi.Auth;

/// <summary>
/// Records one audit event for every request made inside a support session (issue #551, corrected by
/// issue #672): what somebody from outside the business asked for, and what they were actually
/// answered.
///
/// <para><strong>Why it is its own middleware, and why it is the outermost one.</strong> The event
/// was emitted by <see cref="BusinessScopeMiddleware"/> until issue #672, which is too deep in the
/// pipeline for both halves of the question it answers. A request the authorization middleware
/// refuses - a capability the session's set does not hold, or any method that is not a read - never
/// reaches that middleware at all, so the refusals a support trail most needs to show were the ones
/// missing from it. And a request whose endpoint throws unwinds <em>past</em> that middleware before
/// <c>UseExceptionHandler</c> maps the exception to its status, so the event recorded the
/// <c>200</c> the response still held rather than the <c>403</c> or <c>500</c> the caller got.
///
/// Sitting ahead of <c>UseExceptionHandler</c> fixes both at once: every refusal is downstream of
/// here, and by the time <c>_next</c> returns the status is the one that was written. It is the one
/// emitter, so the event happens exactly once per request.</para>
///
/// <para><strong>Attribution is the resolved session, never the header.</strong> The header's
/// presence decides only whether the question is worth asking; the business id and the session row's
/// key come from <see cref="ISupportSessionAccessor"/>, which hashes the presented id and matches it
/// against an active, unexpired session <em>of this same caller's</em>, for a business that is still
/// active. The same memoised answer the capability handler and the business scope acted on, so what
/// was authorised, what was read and what is recorded cannot disagree. A presented session that is
/// not usable, and an ordinary member who sets the header, therefore record nothing at all: there is
/// no verified session to attribute the request to, and attributing it to the business the caller
/// <em>named</em> would be trusting the header as evidence of itself. Why such a session was refused
/// is logged by the accessor, and the refusal itself is the caller's <c>403</c>.</para>
///
/// <para><strong>It carries no result data</strong>: not a response body, not a count, not a path or
/// a query string, and not the session id in either form. The route template rather than the path is
/// the point - <c>api/products/{id}</c> says which endpoint was reached without recording which
/// record - and the administrator's identity and their stated reason stay on the session's start
/// event (issue #550), which this event's key ties the whole trail to.</para>
/// </summary>
public sealed class SupportSessionAuditMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<SupportSessionAuditMiddleware> _logger;

    public SupportSessionAuditMiddleware(RequestDelegate next, ILogger<SupportSessionAuditMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            await _next(context);
        }
        finally
        {
            // In a finally, so a request that failed is recorded with the status it actually got
            // rather than quietly left out of the trail.
            await AuditAsync(context);
        }
    }

    /// <summary>
    /// Writes the event for a request that turned out to be inside a usable session, and nothing at
    /// all for any other request.
    ///
    /// <para>The session is resolved rather than read from somewhere, because this middleware runs
    /// before authentication and cannot ask the question on the way in. It is the same per-request
    /// memoised accessor everything else used, so for every request that reached an endpoint the
    /// answer is already there; for one refused before any consumer asked, it is resolved here, from
    /// the caller's own authenticated identity, which by now exists. It is deliberately not
    /// cancellable: a caller who disconnects must not be able to drop their own audit entry.</para>
    ///
    /// <para>Nothing here may change the response - it has already been written - so a failure is
    /// recorded as the operational problem it is and swallowed. An unrecorded request is worth an
    /// operator seeing; turning it into a second failure for a caller who was already answered is
    /// not.</para>
    /// </summary>
    private async Task AuditAsync(HttpContext context)
    {
        try
        {
            // Presence only, exactly as the member-profile refresh reads it (issue #524): it decides
            // whether to ask the accessor and nothing else. Every request in the application that is
            // not a support request is one dictionary lookup away from leaving this middleware.
            if (!context.Request.Headers.ContainsKey(SupportSessionHeader.Name))
            {
                return;
            }

            var support = await context.RequestServices
                .GetRequiredService<ISupportSessionAccessor>()
                .ResolveAsync(CancellationToken.None);

            if (!support.IsActive)
            {
                return;
            }

            _logger.LogInformation(
                "Support session request. SupportSessionKey={SupportSessionKey} BusinessId={BusinessId} "
                    + "Method={Method} Route={Route} Status={Status} TraceId={TraceId}",
                support.SessionKey,
                support.BusinessId,
                context.Request.Method,
                RouteTemplate(context),
                context.Response.StatusCode,
                Activity.Current?.Id ?? context.TraceIdentifier);
        }
        catch (Exception exception)
        {
            // Value-free on purpose, like every other message on this path: what was read must not
            // reach a log because recording it failed.
            _logger.LogError(
                exception,
                "A support-session request could not be audited. The response is unaffected and the "
                    + "request is missing from the support trail.");
        }
    }

    /// <summary>
    /// The matched endpoint's route template, or <see langword="null"/>. Always the template and
    /// never the path, so no id, name or query value a request carried reaches the trail.
    ///
    /// <para>A handled exception clears the endpoint before the handlers run, so the original is
    /// taken from the exception-handler feature on that path - otherwise the one refusal that is
    /// raised rather than decided would be the one entry that could not say which endpoint it
    /// was.</para>
    /// </summary>
    private static string? RouteTemplate(HttpContext context) =>
        (context.GetEndpoint() ?? context.Features.Get<IExceptionHandlerFeature>()?.Endpoint)
            is RouteEndpoint endpoint
            ? endpoint.RoutePattern.RawText
            : null;
}
