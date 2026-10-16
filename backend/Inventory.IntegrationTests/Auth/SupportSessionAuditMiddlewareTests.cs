using Inventory.Application.SupportSessions;
using InventoryApi.Auth;
using InventoryApi.Tests.Application.SupportSessions;
using InventoryApi.Tests.Http;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace InventoryApi.Tests.Auth;

/// <summary>
/// The support-session audit trail's own middleware (issue #672), exercised directly so that what it
/// decides is visible: which requests it records, with which status, and from which attribution.
///
/// <para>The HTTP-level behaviour over a real database - a refused capability, a refused write, a
/// refusal raised as an exception and an unusable session, each through the whole pipeline - is
/// <c>InventoryApi.Tests.Controllers.SupportSessionRequestApiTests</c>. What is here is the three
/// things only the middleware can be asked directly: that the status it records is read after the
/// rest of the pipeline has finished with the response, that it never swallows what happened
/// downstream, and that a failure to record never becomes the caller's problem.</para>
/// </summary>
public class SupportSessionAuditMiddlewareTests
{
    private const int SupportedBusinessId = 42;
    private const int SessionKey = 7;

    /// <summary>
    /// The criterion: the recorded status is the one the response ended up with, not the one it
    /// started with. Everything that decides a status - the authorization middleware's refusals and
    /// the exception handler's mapping alike - runs downstream of this middleware, so reading it
    /// after <c>_next</c> is what makes "the final mapped status" true for all of them.
    /// </summary>
    [Fact]
    public async Task The_event_records_the_status_the_response_ended_with()
    {
        var logger = new CapturingLogger<SupportSessionAuditMiddleware>();
        var context = Request(ActiveSession());

        await InvokeAsync(context, logger, next: ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        });

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal("403", entry.Properties["Status"]);
        Assert.Equal("GET", entry.Properties["Method"]);
        Assert.Equal(SupportedBusinessId.ToString(), entry.Properties["BusinessId"]);
        Assert.Equal(SessionKey.ToString(), entry.Properties["SupportSessionKey"]);
    }

    /// <summary>
    /// The route <em>template</em>, which says which endpoint was reached without recording which
    /// record: the path the caller asked for appears nowhere in the event.
    /// </summary>
    [Fact]
    public async Task The_event_names_the_route_template_and_not_the_path()
    {
        var logger = new CapturingLogger<SupportSessionAuditMiddleware>();
        var context = Request(ActiveSession());
        context.Request.Path = "/api/products/999999";
        context.SetEndpoint(Endpoint("api/products/{id}"));

        await InvokeAsync(context, logger);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal("api/products/{id}", entry.Properties["Route"]);
        Assert.DoesNotContain("999999", entry.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A handled exception clears the endpoint before the exception handlers run, so without the
    /// fallback to the exception-handler feature the one refusal that is <em>raised</em> rather than
    /// decided would be the one trail entry that could not say which endpoint it was.
    /// </summary>
    [Fact]
    public async Task The_event_still_names_the_endpoint_after_a_handled_exception()
    {
        var logger = new CapturingLogger<SupportSessionAuditMiddleware>();
        var context = Request(ActiveSession());
        context.Features.Set<IExceptionHandlerFeature>(new ExceptionHandlerFeature
        {
            Error = new SupportSessionNayaxUnavailableException(),
            Endpoint = Endpoint("api/machines"),
        });

        await InvokeAsync(context, logger, next: ctx =>
        {
            // What the exception handler leaves behind: the endpoint gone and the mapped status set.
            ctx.SetEndpoint(null);
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        });

        var entry = Assert.Single(logger.Entries);
        Assert.Equal("api/machines", entry.Properties["Route"]);
        Assert.Equal("403", entry.Properties["Status"]);
    }

    /// <summary>
    /// A request that failed outright is still recorded - and the failure is still a failure. The
    /// trail must not be the reason an exception disappears on its way to the exception handler.
    /// </summary>
    [Fact]
    public async Task A_request_that_threw_is_recorded_and_the_exception_is_not_swallowed()
    {
        var logger = new CapturingLogger<SupportSessionAuditMiddleware>();
        var context = Request(ActiveSession());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => InvokeAsync(context, logger, next: _ => throw new InvalidOperationException("boom")));

        Assert.Single(logger.Entries);
    }

    /// <summary>
    /// The criterion: attribution is the resolved session and never the header. A presented session
    /// the server will not honour records nothing - there is no verified business or session row to
    /// name, and naming the one the caller presented would be trusting the header as evidence of
    /// itself.
    /// </summary>
    [Fact]
    public async Task An_unusable_session_records_nothing()
    {
        var logger = new CapturingLogger<SupportSessionAuditMiddleware>();

        await InvokeAsync(Request(FakeSupportSessionAccessor.Ended()), logger);

        Assert.Empty(logger.Entries);
    }

    /// <summary>
    /// And neither does anybody the accessor answers <c>NotRequested</c> for - an ordinary member
    /// who sets the header, whose value is never read at all.
    /// </summary>
    [Fact]
    public async Task A_request_the_accessor_joins_to_no_session_records_nothing()
    {
        var logger = new CapturingLogger<SupportSessionAuditMiddleware>();

        await InvokeAsync(Request(new FakeSupportSessionAccessor()), logger);

        Assert.Empty(logger.Entries);
    }

    /// <summary>
    /// Every request in the application that is not a support request leaves here on one dictionary
    /// lookup: with no header the session is not even asked about, so no lookup is made for the
    /// requests that are the overwhelming majority of them.
    /// </summary>
    [Fact]
    public async Task A_request_without_the_header_asks_nothing_and_records_nothing()
    {
        var logger = new CapturingLogger<SupportSessionAuditMiddleware>();
        var accessor = FakeSupportSessionAccessor.Active(SupportedBusinessId, sessionKey: SessionKey);
        var context = Request(accessor);
        context.Request.Headers.Remove(SupportSessionHeader.Name);

        await InvokeAsync(context, logger);

        Assert.Equal(0, accessor.CallCount);
        Assert.Empty(logger.Entries);
    }

    /// <summary>
    /// Recording happens after the caller has been answered, so a failure in it cannot be made the
    /// caller's problem. It is logged as the operational problem it is - a request missing from the
    /// support trail - and the request is otherwise untouched.
    /// </summary>
    [Fact]
    public async Task A_failure_to_record_never_changes_the_request()
    {
        var logger = new CapturingLogger<SupportSessionAuditMiddleware>();
        var reached = false;

        await InvokeAsync(
            Request(new ThrowingSupportSessionAccessor()),
            logger,
            next: _ =>
            {
                reached = true;
                return Task.CompletedTask;
            });

        Assert.True(reached);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.IsType<InvalidOperationException>(entry.Exception);
    }

    private static Task InvokeAsync(
        HttpContext context,
        ILogger<SupportSessionAuditMiddleware> logger,
        RequestDelegate? next = null) =>
        new SupportSessionAuditMiddleware(next ?? (_ => Task.CompletedTask), logger).InvokeAsync(context);

    private static FakeSupportSessionAccessor ActiveSession() =>
        FakeSupportSessionAccessor.Active(SupportedBusinessId, sessionKey: SessionKey);

    /// <summary>
    /// A <c>GET</c> presenting a session id, with <paramref name="accessor"/> as the one thing that
    /// decides what that id is worth - exactly as the request's own memoised accessor does.
    /// </summary>
    private static DefaultHttpContext Request(ISupportSessionAccessor accessor)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton(accessor)
                .BuildServiceProvider(),
        };
        context.Request.Method = HttpMethods.Get;
        context.Request.Headers[SupportSessionHeader.Name] = "the-presented-session-id";

        return context;
    }

    private static RouteEndpoint Endpoint(string template) => new(
        _ => Task.CompletedTask,
        RoutePatternFactory.Parse(template),
        order: 0,
        EndpointMetadataCollection.Empty,
        displayName: template);

    private sealed class ThrowingSupportSessionAccessor : ISupportSessionAccessor
    {
        public Task<SupportSessionAccess> ResolveAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The session could not be resolved.");
    }
}
