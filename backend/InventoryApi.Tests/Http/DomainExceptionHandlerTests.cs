using System.Text;
using System.Text.Json;
using Inventory.Application.Exceptions;
using InventoryApi.Http;
using InventoryApi.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace InventoryApi.Tests.Http;

public class DomainExceptionHandlerTests
{
    [Fact]
    public async Task Domain_validation_exception_becomes_a_400_problem_json_response()
    {
        var (handler, context, body, logger) = CreateHandler();

        var handled = await handler.TryHandleAsync(
            context, new DomainValidationException("Correction quantity must remove stock."), CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.StartsWith("application/problem+json", context.Response.ContentType, StringComparison.Ordinal);

        var root = ParseBody(body);
        Assert.Equal("Request validation failed", root.GetProperty("title").GetString());
        Assert.Equal("Correction quantity must remove stock.", root.GetProperty("detail").GetString());
        Assert.Equal("Correction quantity must remove stock.", root.GetProperty("message").GetString());
        Assert.Equal(400, root.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("traceId").GetString()));

        // An expected validation outcome is not a server error.
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task Domain_conflict_exception_becomes_a_409_problem_json_response_and_logs_a_warning()
    {
        var (handler, context, body, logger) = CreateHandler();

        var handled = await handler.TryHandleAsync(
            context,
            new DomainConflictException("The agreement overlaps an existing agreement for this site."),
            CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status409Conflict, context.Response.StatusCode);

        var root = ParseBody(body);
        Assert.Equal("The request conflicts with the current state", root.GetProperty("title").GetString());
        Assert.Equal("The agreement overlaps an existing agreement for this site.", root.GetProperty("detail").GetString());
        Assert.Equal(409, root.GetProperty("status").GetInt32());

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("The agreement overlaps an existing agreement for this site.", entry.Message, StringComparison.Ordinal);
        // A conflict is an expected outcome, not a server error, so it must never be logged at Error.
        Assert.NotEqual(LogLevel.Error, entry.Level);
    }

    [Theory]
    [InlineData(typeof(ArgumentException))]
    [InlineData(typeof(InvalidOperationException))]
    public async Task Legacy_exception_types_keep_their_established_400_mapping(Type exceptionType)
    {
        var (handler, context, body, logger) = CreateHandler();
        var exception = (Exception)Activator.CreateInstance(exceptionType, "Existing validation message.")!;

        var handled = await handler.TryHandleAsync(context, exception, CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        var root = ParseBody(body);
        Assert.Equal("Existing validation message.", root.GetProperty("detail").GetString());
        Assert.Equal("Existing validation message.", root.GetProperty("message").GetString());
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task Insufficient_stock_exception_keeps_its_established_400_mapping()
    {
        var (handler, context, body, _) = CreateHandler();

        var handled = await handler.TryHandleAsync(
            context, new InsufficientStockException(2), CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        var root = ParseBody(body);
        Assert.Equal("Not enough products in stock. Available stock: 2", root.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Inventory_cost_data_quality_exception_is_not_claimed_here()
    {
        // It derives from InvalidOperationException but signals a data-integrity problem found
        // while rebuilding costing history, not a routine client validation failure. Today
        // nothing catches it, so it already surfaces as an unexpected error; this handler must
        // not silently reclassify it as a client-caused 400.
        var (handler, context, body, logger) = CreateHandler();

        var handled = await handler.TryHandleAsync(
            context, new InventoryCostDataQualityException("Ledger is inconsistent."), CancellationToken.None);

        Assert.False(handled);
        Assert.NotEqual(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Empty(ReadBody(body));
        Assert.Empty(logger.Entries);
    }

    [Theory]
    [InlineData(typeof(NotImplementedException))]
    [InlineData(typeof(OperationCanceledException))]
    public async Task Unrelated_exceptions_are_left_to_the_next_handler(Type exceptionType)
    {
        var (handler, context, body, logger) = CreateHandler();
        var exception = (Exception)Activator.CreateInstance(exceptionType)!;

        var handled = await handler.TryHandleAsync(context, exception, CancellationToken.None);

        Assert.False(handled);
        Assert.Empty(ReadBody(body));
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task Public_response_carries_no_exception_type_name_or_stack_trace()
    {
        var (handler, context, body, _) = CreateHandler();
        Exception exception;
        try
        {
            throw new DomainValidationException("Quantity must be positive.");
        }
        catch (DomainValidationException caught)
        {
            exception = caught;
        }

        await handler.TryHandleAsync(context, exception, CancellationToken.None);

        var payload = ReadBody(body);
        Assert.DoesNotContain("DomainValidationException", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("at InventoryApi", payload, StringComparison.Ordinal);
        Assert.DoesNotContain(".cs:", payload, StringComparison.OrdinalIgnoreCase);
    }

    private static JsonElement ParseBody(MemoryStream body) => JsonDocument.Parse(ReadBody(body)).RootElement;

    private static string ReadBody(MemoryStream body) => Encoding.UTF8.GetString(body.ToArray());

    private static (DomainExceptionHandler Handler, DefaultHttpContext Context, MemoryStream Body, CapturingLogger Logger) CreateHandler()
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
        var handler = new DomainExceptionHandler(provider.GetRequiredService<IProblemDetailsService>(), logger);

        return (handler, context, body, logger);
    }

    private sealed class CapturingLogger : ILogger<DomainExceptionHandler>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
