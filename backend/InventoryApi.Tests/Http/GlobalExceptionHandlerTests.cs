using System.Text;
using System.Text.Json;
using InventoryApi.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace InventoryApi.Tests.Http;

public class GlobalExceptionHandlerTests
{
    /// <summary>
    /// A non-secret sentinel standing in for whatever sensitive text an internal exception message
    /// might carry. It is deliberately not shaped like a credential or connection string - a
    /// realistic-looking fake trips repository secret scanning without making the assertion any
    /// stronger. All these tests need is a distinctive string that must never leave the server.
    /// </summary>
    private const string SensitiveDetail = "sensitive-detail-sentinel-must-not-be-published";

    [Fact]
    public async Task Unexpected_exception_becomes_a_generic_500_problem_json_response()
    {
        var (handler, context, body, logger) = CreateHandler();

        var handled = await handler.TryHandleAsync(
            context, new InvalidOperationException($"Connection failed: {SensitiveDetail}"), CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.StartsWith("application/problem+json", context.Response.ContentType, StringComparison.Ordinal);

        var payload = ReadBody(body);
        var root = JsonDocument.Parse(payload).RootElement;
        Assert.Equal("An unexpected error occurred", root.GetProperty("title").GetString());
        Assert.Equal(
            "The request could not be completed. Contact support with the trace identifier if this persists.",
            root.GetProperty("detail").GetString());
        Assert.Equal(500, root.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("traceId").GetString()));

        // The public response must never carry the exception's message, type name, or a stack trace.
        Assert.DoesNotContain(SensitiveDetail, payload, StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidOperationException", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("Connection failed", payload, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(typeof(ArgumentException))]
    [InlineData(typeof(InvalidOperationException))]
    public async Task Arbitrary_framework_exceptions_become_a_logged_generic_500_without_their_message(Type exceptionType)
    {
        // DomainExceptionHandler deliberately leaves ArgumentException/InvalidOperationException
        // unclaimed so an unrelated internal failure - a tenant-scope double resolve, a report
        // export argument check - stays a loud, logged 500 instead of becoming a public 400 that
        // echoes a developer-facing message.
        var (handler, context, body, logger) = CreateHandler();
        var exception = (Exception)Activator.CreateInstance(exceptionType, SensitiveDetail)!;

        var handled = await handler.TryHandleAsync(context, exception, CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);

        var payload = ReadBody(body);
        Assert.DoesNotContain(SensitiveDetail, payload, StringComparison.Ordinal);
        Assert.DoesNotContain(exceptionType.Name, payload, StringComparison.Ordinal);
        Assert.Equal(
            "The request could not be completed. Contact support with the trace identifier if this persists.",
            JsonDocument.Parse(payload).RootElement.GetProperty("detail").GetString());

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Same(exception, entry.Exception);
    }

    [Fact]
    public async Task Unexpected_exception_is_logged_exactly_once_at_error_with_the_response_trace_id()
    {
        var (handler, context, body, logger) = CreateHandler();
        context.TraceIdentifier = "trace-for-this-request";
        var exception = new InvalidOperationException($"Connection failed: {SensitiveDetail}");

        await handler.TryHandleAsync(context, exception, CancellationToken.None);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Same(exception, entry.Exception);
        Assert.Contains("trace-for-this-request", entry.Message, StringComparison.Ordinal);

        var traceId = JsonDocument.Parse(ReadBody(body)).RootElement.GetProperty("traceId").GetString();
        Assert.Equal("trace-for-this-request", traceId);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_logged_as_an_unexpected_error()
    {
        var (handler, context, body, logger) = CreateHandler();

        var handled = await handler.TryHandleAsync(context, new OperationCanceledException(), CancellationToken.None);

        Assert.False(handled);
        Assert.Empty(logger.Entries);
        Assert.Empty(ReadBody(body));
    }

    private static string ReadBody(MemoryStream body) => Encoding.UTF8.GetString(body.ToArray());

    private static (GlobalExceptionHandler Handler, DefaultHttpContext Context, MemoryStream Body, CapturingLogger Logger) CreateHandler()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddProblemDetails();
        var provider = services.BuildServiceProvider();

        var body = new MemoryStream();
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Response.Body = body;
        context.Request.Headers.Accept = "application/json";

        var logger = new CapturingLogger();
        var handler = new GlobalExceptionHandler(provider.GetRequiredService<IProblemDetailsService>(), logger);

        return (handler, context, body, logger);
    }

    private sealed class CapturingLogger : ILogger<GlobalExceptionHandler>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception), exception));
        }
    }
}
