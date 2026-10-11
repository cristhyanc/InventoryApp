using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Inventory.Application.Nayax;
using Inventory.Domain.Nayax;
using Inventory.Infrastructure.Nayax;
using InventoryApi.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace InventoryApi.Tests.Http;

/// <summary>
/// The HTTP boundary for the per-business Nayax connection failures (issue #520): the stable error
/// the frontend shows when the current business's Nayax connection cannot be used.
///
/// The status code is part of the contract and is deliberately not an authorization status: a
/// signed-in member whose business has not connected Nayax is perfectly entitled to the feature, so
/// answering 401 or 403 would describe the caller rather than the configuration.
/// </summary>
public class NayaxConnectionExceptionHandlerTests
{
    private const string FakeToken = "fake-token-not-a-real-credential";

    [Theory]
    [InlineData(NayaxConnectionStatus.NotConfigured)]
    [InlineData(NayaxConnectionStatus.NeedsAttention)]
    public async Task A_not_connected_failure_becomes_a_stable_409_problem_json_response(
        NayaxConnectionStatus status)
    {
        var (handler, context, body, _) = CreateHandler();

        var handled = await handler.TryHandleAsync(
            context, new NayaxNotConnectedException(status), CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status409Conflict, context.Response.StatusCode);
        Assert.StartsWith("application/problem+json", context.Response.ContentType, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(ReadBody(body));
        var root = document.RootElement;
        Assert.Equal("Nayax is not connected", root.GetProperty("title").GetString());
        Assert.Equal(NayaxNotConnectedException.StableMessage, root.GetProperty("detail").GetString());
        Assert.Equal(NayaxNotConnectedException.StableMessage, root.GetProperty("message").GetString());
        Assert.Equal("nayax_not_connected", root.GetProperty("code").GetString());
        Assert.Equal(status.ToString(), root.GetProperty("nayaxConnectionStatus").GetString());
        Assert.Equal(409, root.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("traceId").GetString()));
    }

    [Fact]
    public async Task A_permission_failure_becomes_its_own_stable_409_response()
    {
        var (handler, context, body, _) = CreateHandler();

        var handled = await handler.TryHandleAsync(
            context, new NayaxPermissionNotGrantedException("GetMachinesAsync"), CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status409Conflict, context.Response.StatusCode);

        using var document = JsonDocument.Parse(ReadBody(body));
        var root = document.RootElement;
        Assert.Equal("Nayax permission not granted", root.GetProperty("title").GetString());
        Assert.Equal(NayaxPermissionNotGrantedException.StableMessage, root.GetProperty("detail").GetString());
        Assert.Equal("nayax_permission_not_granted", root.GetProperty("code").GetString());
        // The refused operation is an internal diagnostic: logged, never published.
        Assert.False(root.TryGetProperty("nayaxConnectionStatus", out _));
        Assert.DoesNotContain("GetMachinesAsync", ReadBody(body), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_response_carries_no_operator_id_token_or_internal_detail()
    {
        var (handler, context, body, _) = CreateHandler();

        await handler.TryHandleAsync(
            context, new NayaxNotConnectedException(NayaxConnectionStatus.NeedsAttention), CancellationToken.None);

        var payload = ReadBody(body);
        Assert.DoesNotContain(FakeToken, payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bearer", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Authorization", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("at InventoryApi", payload, StringComparison.Ordinal);
        Assert.DoesNotContain(".cs", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NayaxNotConnectedException", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_failure_is_logged_once_at_warning_with_safe_structured_context()
    {
        var (handler, context, body, logger) = CreateHandler();
        context.TraceIdentifier = "trace-id-for-this-request";
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/api/machines";

        var previous = Activity.Current;
        Activity.Current = null;
        try
        {
            await handler.TryHandleAsync(
                context, new NayaxNotConnectedException(NayaxConnectionStatus.NotConfigured), CancellationToken.None);
        }
        finally
        {
            Activity.Current = previous;
        }

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Null(entry.Exception);
        Assert.Equal("nayax_not_connected", entry.Properties["NayaxErrorCode"]);
        Assert.Equal("NotConfigured", entry.Properties["NayaxConnectionDetail"]);
        Assert.Equal("GET", entry.Properties["Method"]);
        Assert.Equal("/api/machines", entry.Properties["Path"]);

        var responseTraceId = JsonDocument.Parse(ReadBody(body)).RootElement.GetProperty("traceId").GetString();
        Assert.Equal("trace-id-for-this-request", entry.Properties["TraceId"]);
        Assert.Equal(responseTraceId, entry.Properties["TraceId"]);
    }

    /// <summary>
    /// The refused operation is what an operator needs in order to find which Nayax permission is
    /// missing, so it is logged even though it is not published.
    /// </summary>
    [Fact]
    public async Task A_permission_failure_logs_the_operation_it_refused()
    {
        var (handler, context, _, logger) = CreateHandler();

        await handler.TryHandleAsync(
            context, new NayaxPermissionNotGrantedException("GetProductsAsync"), CancellationToken.None);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal("nayax_permission_not_granted", entry.Properties["NayaxErrorCode"]);
        Assert.Equal("GetProductsAsync", entry.Properties["NayaxConnectionDetail"]);
    }

    /// <summary>
    /// An upstream Nayax failure is a different handler's, and the usual framework-wide types stay
    /// with <c>GlobalExceptionHandler</c>'s logged, generic 500.
    /// </summary>
    [Fact]
    public async Task An_upstream_nayax_failure_is_not_claimed_here()
    {
        var (handler, context, body, logger) = CreateHandler();

        var handled = await handler.TryHandleAsync(
            context,
            new NayaxUpstreamException("GetMachinesAsync", HttpMethod.Get, "machines", HttpStatusCode.Forbidden),
            CancellationToken.None);

        Assert.False(handled);
        Assert.NotEqual(StatusCodes.Status409Conflict, context.Response.StatusCode);
        Assert.Empty(ReadBody(body));
        Assert.Empty(logger.Entries);
    }

    [Theory]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(ArgumentException))]
    [InlineData(typeof(OperationCanceledException))]
    public async Task Unrelated_exceptions_are_not_claimed(Type exceptionType)
    {
        var (handler, context, body, logger) = CreateHandler();
        var exception = (Exception)Activator.CreateInstance(exceptionType)!;

        var handled = await handler.TryHandleAsync(context, exception, CancellationToken.None);

        Assert.False(handled);
        Assert.Empty(ReadBody(body));
        Assert.Empty(logger.Entries);
    }

    private static (
        NayaxConnectionExceptionHandler Handler,
        DefaultHttpContext Context,
        MemoryStream Body,
        CapturingLogger<NayaxConnectionExceptionHandler> Logger) CreateHandler()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddProblemDetails();
        var provider = services.BuildServiceProvider();

        var body = new MemoryStream();
        var context = new DefaultHttpContext
        {
            RequestServices = provider,
        };
        context.Response.Body = body;
        context.Request.Headers.Accept = "application/json";

        var logger = new CapturingLogger<NayaxConnectionExceptionHandler>();
        var handler = new NayaxConnectionExceptionHandler(
            provider.GetRequiredService<IProblemDetailsService>(),
            logger);

        return (handler, context, body, logger);
    }

    private static string ReadBody(MemoryStream body) => Encoding.UTF8.GetString(body.ToArray());
}
