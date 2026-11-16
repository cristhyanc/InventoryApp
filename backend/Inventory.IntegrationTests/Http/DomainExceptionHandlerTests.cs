using System.Text;
using System.Text.Json;
using Inventory.Application.Costing;
using Inventory.Domain.Exceptions;
using Inventory.Infrastructure.Data;
using InventoryApi.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace InventoryApi.Tests.Http;

public class DomainExceptionHandlerTests
{
    /// <summary>
    /// A non-secret sentinel standing in for any internal, developer-facing exception text. It is
    /// deliberately not a credential, connection string, or token: the assertions only need a
    /// distinctive string that must never appear in a public response body.
    /// </summary>
    private const string InternalDetail = "internal-detail-must-not-be-published";

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

    /// <summary>
    /// The membership refusals of issue #522 travel this same path, which is what makes "a stable
    /// 409" true rather than planned: <c>MembershipWriteGuard</c> throws
    /// <see cref="DomainConflictException"/> with one of two fixed Domain messages, and this
    /// handler publishes it verbatim as <c>409</c>. Asserted here, on the real handler, because
    /// the endpoints that will raise it arrive in later tasks (#504, #507, #509) and the status
    /// code must not be theirs to choose.
    /// </summary>
    [Theory]
    [InlineData("This person is already a member of a business.")]
    [InlineData("A business must keep at least one active Owner. Give another member the Owner role first.")]
    public async Task A_membership_conflict_becomes_a_409_carrying_its_own_message(string message)
    {
        var (handler, context, body, _) = CreateHandler();

        var handled = await handler.TryHandleAsync(
            context, new DomainConflictException(message), CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status409Conflict, context.Response.StatusCode);

        var root = ParseBody(body);
        Assert.Equal(message, root.GetProperty("detail").GetString());
        Assert.Equal(message, root.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData(typeof(ArgumentException))]
    [InlineData(typeof(ArgumentNullException))]
    [InlineData(typeof(ArgumentOutOfRangeException))]
    [InlineData(typeof(InvalidOperationException))]
    public async Task Arbitrary_framework_exceptions_are_not_claimed_and_never_reach_the_caller(Type exceptionType)
    {
        // These types are thrown all over the BCL, EF Core, and this application's own
        // infrastructure (tenant scope resolution, costing data quality, report export). Their
        // messages are written for a developer, not an API client, so this handler must never
        // claim them: they belong to GlobalExceptionHandler's logged, generic 500.
        var (handler, context, body, logger) = CreateHandler();
        var exception = (Exception)Activator.CreateInstance(exceptionType, InternalDetail)!;

        var handled = await handler.TryHandleAsync(context, exception, CancellationToken.None);

        Assert.False(handled);
        Assert.NotEqual(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        var payload = ReadBody(body);
        Assert.Empty(payload);
        Assert.DoesNotContain(InternalDetail, payload, StringComparison.Ordinal);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task A_subclass_of_a_framework_exception_is_not_claimed_either()
    {
        // The tenant-scope double-resolve guard (BusinessScope.Resolve) and any other internal
        // invariant check must stay a loud 500, not become a silent 400 echoing its message.
        var (handler, context, body, logger) = CreateHandler();

        var handled = await handler.TryHandleAsync(
            context, new ObjectDisposedException(InternalDetail), CancellationToken.None);

        Assert.False(handled);
        Assert.Empty(ReadBody(body));
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task Insufficient_stock_exception_keeps_its_established_400_mapping()
    {
        var (handler, context, body, logger) = CreateHandler();

        var handled = await handler.TryHandleAsync(
            context, new InsufficientStockException(2), CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        var root = ParseBody(body);
        Assert.Equal("Not enough products in stock. Available stock: 2", root.GetProperty("detail").GetString());

        // Issue #163: InsufficientStockException now derives from DomainConflictException (see
        // Inventory.Domain.Exceptions.InsufficientStockException), but its established mapping -
        // 400, not logged - predates that inheritance and must not change because of it. Without
        // DomainExceptionHandler's explicit exclusion, this would incorrectly pick up
        // DomainConflictException's "log a warning" behaviour.
        Assert.Empty(logger.Entries);
    }

    /// <summary>
    /// Issue #1240 replaced this type's mapping: it used to be unclaimed here, which made every
    /// refused stock write a generic <c>500</c> with no product name. Its message is the replay's
    /// own product-named sentence, written for the operator who has to repair that product, and the
    /// refusal depends on the product's current history - so it is published verbatim as a
    /// <c>409</c>, the status <see cref="DomainConflictException"/> already carries, and is logged
    /// once at <see cref="LogLevel.Warning"/> rather than disappearing with the <c>500</c>.
    /// </summary>
    [Fact]
    public async Task Inventory_cost_data_quality_exception_becomes_a_409_carrying_its_product_named_message()
    {
        const string Refusal = "Restock adjustment 12 for product Coke (7) has no valid unit cost.";
        var (handler, context, body, logger) = CreateHandler();

        var handled = await handler.TryHandleAsync(
            context, new InventoryCostDataQualityException(Refusal), CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status409Conflict, context.Response.StatusCode);
        Assert.StartsWith("application/problem+json", context.Response.ContentType, StringComparison.Ordinal);

        var root = ParseBody(body);
        Assert.Equal("The request conflicts with the current state", root.GetProperty("title").GetString());
        Assert.Equal(Refusal, root.GetProperty("detail").GetString());
        Assert.Equal(Refusal, root.GetProperty("message").GetString());
        Assert.Equal(409, root.GetProperty("status").GetInt32());

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains(Refusal, entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_plain_invalid_operation_exception_is_still_not_claimed_beside_that_subclass()
    {
        // The subclass above is claimed by name. Its base type must stay unclaimed: claiming it
        // would publish the internal message of every InvalidOperationException in the BCL, EF Core
        // and this application's own infrastructure.
        var (handler, context, body, logger) = CreateHandler();

        var handled = await handler.TryHandleAsync(
            context, new InvalidOperationException(InternalDetail), CancellationToken.None);

        Assert.False(handled);
        Assert.Empty(ReadBody(body));
        Assert.DoesNotContain(InternalDetail, ReadBody(body), StringComparison.Ordinal);
        Assert.Empty(logger.Entries);
    }

    /// <summary>
    /// The tenant-isolation refusal, named here rather than left to the generic cases above: issue
    /// #1240 widened this handler by one type, and a cross-business write must still be the loud,
    /// logged, message-free <c>500</c> <c>GlobalExceptionHandler</c> gives it.
    /// </summary>
    [Fact]
    public async Task A_cross_business_access_refusal_is_still_left_to_the_global_handler()
    {
        var (handler, context, body, logger) = CreateHandler();

        var handled = await handler.TryHandleAsync(
            context, new CrossBusinessAccessException(InternalDetail), CancellationToken.None);

        Assert.False(handled);
        Assert.NotEqual(StatusCodes.Status409Conflict, context.Response.StatusCode);
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
