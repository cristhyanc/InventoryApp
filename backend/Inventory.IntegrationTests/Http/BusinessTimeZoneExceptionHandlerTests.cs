using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Inventory.Application.Time;
using InventoryApi.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace InventoryApi.Tests.Http;

/// <summary>
/// The HTTP boundary for a business whose time zone cannot be resolved (issue #499), with the stable
/// error code issue #681 added.
///
/// <para>Before that code the refusal was indistinguishable from any other server fault: a client
/// could not tell "this business has no usable time zone, which an Owner can correct" from "a null
/// reference somewhere". These tests pin the two halves that matter - the code is there, and the
/// stored time-zone id is not.</para>
/// </summary>
public class BusinessTimeZoneExceptionHandlerTests
{
    private const string StoredZoneId = "Mars/Olympus_Mons";

    [Fact]
    public async Task An_unresolvable_business_time_zone_becomes_a_500_carrying_the_stable_code()
    {
        var (handler, context, body, _) = CreateHandler();

        var handled = await handler.TryHandleAsync(
            context, new BusinessTimeZoneUnavailableException(StoredZoneId), CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.StartsWith("application/problem+json", context.Response.ContentType, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(ReadBody(body));
        var root = document.RootElement;
        Assert.Equal("business_time_zone_unavailable", root.GetProperty("code").GetString());
        Assert.Equal("The business time zone is unavailable", root.GetProperty("title").GetString());
        Assert.Equal(500, root.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("traceId").GetString()));
        // Kept beside `detail` because existing frontend error handling reads `message`.
        Assert.Equal(
            root.GetProperty("detail").GetString(),
            root.GetProperty("message").GetString());
        Assert.Contains("Business Settings", root.GetProperty("detail").GetString()!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The other cause of the same exception - no business resolved at all - gets the same code: a
    /// client has nothing different to do about it, and distinguishing them would publish whether a
    /// business was resolved.
    /// </summary>
    [Fact]
    public async Task A_request_with_no_resolved_business_gets_the_same_code()
    {
        var (handler, context, body, _) = CreateHandler();

        await handler.TryHandleAsync(
            context, new BusinessTimeZoneUnavailableException(null), CancellationToken.None);

        using var document = JsonDocument.Parse(ReadBody(body));
        Assert.Equal("business_time_zone_unavailable", document.RootElement.GetProperty("code").GetString());
    }

    /// <summary>
    /// The stored id is configuration an Owner typed, and the exception message names it, so neither
    /// may reach the response - only the server log, where the operator who has to correct it reads.
    /// </summary>
    [Fact]
    public async Task The_response_carries_neither_the_stored_zone_id_nor_the_exception()
    {
        var (handler, context, body, _) = CreateHandler();

        await handler.TryHandleAsync(
            context, new BusinessTimeZoneUnavailableException(StoredZoneId), CancellationToken.None);

        var payload = ReadBody(body);
        Assert.DoesNotContain(StoredZoneId, payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Olympus", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("BusinessTimeZoneUnavailableException", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("at InventoryApi", payload, StringComparison.Ordinal);
        Assert.DoesNotContain(".cs", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_failure_is_logged_once_with_the_code_and_the_stored_id_for_an_operator()
    {
        var (handler, context, body, logger) = CreateHandler();
        context.TraceIdentifier = "trace-id-for-this-request";
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/api/reports/daily";

        var previous = Activity.Current;
        Activity.Current = null;
        try
        {
            await handler.TryHandleAsync(
                context, new BusinessTimeZoneUnavailableException(StoredZoneId), CancellationToken.None);
        }
        finally
        {
            Activity.Current = previous;
        }

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal("business_time_zone_unavailable", entry.Properties["ErrorCode"]);
        Assert.Equal("GET", entry.Properties["Method"]);
        Assert.Equal("/api/reports/daily", entry.Properties["Path"]);

        // The id to correct is in the log, through the exception's own message.
        Assert.Contains(StoredZoneId, entry.Exception?.Message ?? string.Empty, StringComparison.Ordinal);

        var responseTraceId = JsonDocument.Parse(ReadBody(body)).RootElement.GetProperty("traceId").GetString();
        Assert.Equal("trace-id-for-this-request", entry.Properties["TraceId"]);
        Assert.Equal(responseTraceId, entry.Properties["TraceId"]);
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
        BusinessTimeZoneExceptionHandler Handler,
        DefaultHttpContext Context,
        MemoryStream Body,
        CapturingLogger<BusinessTimeZoneExceptionHandler> Logger) CreateHandler()
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

        var logger = new CapturingLogger<BusinessTimeZoneExceptionHandler>();
        var handler = new BusinessTimeZoneExceptionHandler(
            provider.GetRequiredService<IProblemDetailsService>(),
            logger);

        return (handler, context, body, logger);
    }

    private static string ReadBody(MemoryStream body) => Encoding.UTF8.GetString(body.ToArray());
}
