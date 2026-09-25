using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Inventory.Infrastructure.Nayax;
using InventoryApi.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InventoryApi.Tests.Http;

public class NayaxUpstreamExceptionHandlerTests
{
    private const string FakeToken = "fake-token-not-a-real-credential";
    private const string SensitiveUpstreamBody =
        "{\"error\":\"denied\",\"secretCustomerRef\":\"SHOULD-NEVER-SURFACE\"}";

    [Fact]
    public async Task Nayax_upstream_exception_becomes_a_502_problem_json_response()
    {
        var (handler, context, body) = CreateHandler();

        var handled = await handler.TryHandleAsync(context, NayaxFailure(HttpStatusCode.Forbidden), CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status502BadGateway, context.Response.StatusCode);
        Assert.StartsWith("application/problem+json", context.Response.ContentType, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(ReadBody(body));
        var root = document.RootElement;
        Assert.Equal("Nayax service error", root.GetProperty("title").GetString());
        Assert.Equal("The Nayax service could not complete the request.", root.GetProperty("detail").GetString());
        Assert.Equal(502, root.GetProperty("status").GetInt32());

        var traceId = root.GetProperty("traceId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(traceId));
    }

    [Fact]
    public async Task Trace_id_is_the_current_activity_id_when_one_is_active()
    {
        var (handler, context, body) = CreateHandler();
        var previous = Activity.Current;
        using var activity = new Activity("nayax-upstream-test").Start();
        try
        {
            await handler.TryHandleAsync(context, NayaxFailure(HttpStatusCode.Forbidden), CancellationToken.None);
        }
        finally
        {
            Activity.Current = previous;
        }

        using var document = JsonDocument.Parse(ReadBody(body));
        Assert.Equal(activity.Id, document.RootElement.GetProperty("traceId").GetString());
    }

    [Fact]
    public async Task Trace_id_falls_back_to_the_request_trace_identifier()
    {
        var (handler, context, body) = CreateHandler();
        context.TraceIdentifier = "trace-id-for-this-request";

        var previous = Activity.Current;
        Activity.Current = null;
        try
        {
            await handler.TryHandleAsync(context, NayaxFailure(HttpStatusCode.InternalServerError), CancellationToken.None);
        }
        finally
        {
            Activity.Current = previous;
        }

        using var document = JsonDocument.Parse(ReadBody(body));
        Assert.Equal("trace-id-for-this-request", document.RootElement.GetProperty("traceId").GetString());
    }

    [Fact]
    public async Task Upstream_500_is_also_reported_as_502()
    {
        var (handler, context, _) = CreateHandler();

        var handled = await handler.TryHandleAsync(context, NayaxFailure(HttpStatusCode.InternalServerError), CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status502BadGateway, context.Response.StatusCode);
    }

    [Fact]
    public async Task Public_response_leaks_no_token_body_stack_trace_or_internal_path()
    {
        var (handler, context, body) = CreateHandler();

        // An exception that genuinely carries a stack trace and a sensitive inner message,
        // to prove none of it reaches the client.
        Exception inner;
        try
        {
            throw new HttpRequestException(
                $"Authorization: Bearer {FakeToken} rejected. Upstream said {SensitiveUpstreamBody}");
        }
        catch (HttpRequestException caught)
        {
            inner = caught;
        }

        var exception = new NayaxUpstreamException(
            "GetMachinesAsync", HttpMethod.Get, "machines", HttpStatusCode.Forbidden, inner);

        await handler.TryHandleAsync(context, exception, CancellationToken.None);

        var payload = ReadBody(body);
        Assert.DoesNotContain(FakeToken, payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Authorization", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bearer", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SHOULD-NEVER-SURFACE", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secretCustomerRef", payload, StringComparison.OrdinalIgnoreCase);

        // No stack trace and no internal source path.
        Assert.DoesNotContain("at InventoryApi", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpRequestException", payload, StringComparison.Ordinal);
        Assert.DoesNotContain(".cs", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("C:\\", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NayaxUpstreamException", payload, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(ArgumentException))]
    [InlineData(typeof(OperationCanceledException))]
    public async Task Unrelated_exceptions_are_not_handled_as_nayax_failures(Type exceptionType)
    {
        var (handler, context, body) = CreateHandler();
        var exception = (Exception)Activator.CreateInstance(exceptionType)!;

        var handled = await handler.TryHandleAsync(context, exception, CancellationToken.None);

        Assert.False(handled);
        Assert.NotEqual(StatusCodes.Status502BadGateway, context.Response.StatusCode);
        Assert.Empty(ReadBody(body));
    }

    private static NayaxUpstreamException NayaxFailure(HttpStatusCode status) =>
        new("GetMachinesAsync", HttpMethod.Get, "machines", status);

    private static (NayaxUpstreamExceptionHandler Handler, DefaultHttpContext Context, MemoryStream Body) CreateHandler()
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

        var handler = new NayaxUpstreamExceptionHandler(
            provider.GetRequiredService<IProblemDetailsService>());

        return (handler, context, body);
    }

    private static string ReadBody(MemoryStream body) => Encoding.UTF8.GetString(body.ToArray());
}
