namespace InventoryApi.Http.HealthChecks;

/// <summary>
/// Marks an endpoint whose responses carry the build's commit in an <c>X-Build-Sha</c> header.
/// <c>Program.cs</c> attaches <see cref="Instance"/> to the two <c>MapHealthChecks</c>
/// registrations, and <see cref="BuildShaHeaderMiddleware"/> looks for it on the endpoint routing
/// selected.
///
/// <para>The marker is what binds the header to the endpoints rather than to two spellings of
/// their paths: ASP.NET Core matches a route template case-insensitively and ignores a trailing
/// slash, so <c>/HEALTH/READY</c> and <c>/health/ready/</c> reach the same readiness endpoint and
/// must carry the same commit. Anything the health registrations do not carry this marker on - a
/// controller, or a path no endpoint matches at all - is untouched.</para>
/// </summary>
public sealed class BuildShaHeaderMetadata
{
    public static readonly BuildShaHeaderMetadata Instance = new();

    private BuildShaHeaderMetadata()
    {
    }
}

/// <summary>
/// Issue #1308 (audit OPS-04, #1228/#1231): puts the build's commit on the two health endpoints'
/// responses in an <c>X-Build-Sha</c> header, so an operator can tell a reply from the old process
/// finishing its restart apart from the new release instead of trusting the first 200/503. The
/// value arrives as the registered <see cref="BuildShaSource"/>, which <c>Program.cs</c> builds
/// from <see cref="BuildMetadata.BuildSha"/> at startup, so the assembly is read exactly once and
/// a test host can substitute a build that carries no commit at all.
///
/// <para>It is its own middleware rather than a lambda in <c>Program.cs</c> for the same reason
/// every other middleware in this pipeline is: the composition root registers the pipeline, it does
/// not implement request behaviour. The header name lives here, next to the
/// <see cref="BuildMetadata"/> it belongs to, where
/// <c>HealthEndpointsTests</c>/<c>HealthEndpointsDatabaseUnavailableTests</c> pin it on both
/// endpoints, on every route variant, and on a 503.</para>
///
/// <para>Which responses get the header is decided by the endpoint routing selected, not by
/// comparing the raw request path: the registrations in <c>Program.cs</c> carry
/// <see cref="BuildShaHeaderMetadata"/>, and this reads it back. Comparing the path would miss
/// every request routing still sends to those endpoints under a different spelling - a case
/// variant or a trailing slash - and answer a probe with the endpoint's normal status and body but
/// no commit. Routing runs ahead of this middleware (<c>WebApplication</c> puts
/// <c>UseRouting</c> at the front of the pipeline), which is the same thing
/// <c>app.UseAuthorization()</c> above relies on.</para>
///
/// <para><strong>It only adds a header.</strong> Every request passes through to <c>_next</c>
/// untouched, and a request for any other endpoint leaves on one metadata lookup: nothing here
/// reads or writes a status code, a body, or anything else about the request, and neither
/// endpoint's <c>AllowAnonymous()</c> is affected. Nothing but the commit SHA (or
/// <c>"unknown"</c>) is exposed - no environment name, machine name, configuration or dependency
/// version.</para>
/// </summary>
public sealed class BuildShaHeaderMiddleware
{
    public const string HeaderName = "X-Build-Sha";

    private readonly RequestDelegate _next;
    private readonly string _buildSha;

    public BuildShaHeaderMiddleware(RequestDelegate next, BuildShaSource buildSha)
    {
        ArgumentNullException.ThrowIfNull(buildSha);

        _next = next;
        _buildSha = buildSha.BuildSha;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (ReportsBuildSha(context.GetEndpoint()))
        {
            context.Response.Headers[HeaderName] = _buildSha;
        }

        await _next(context);
    }

    private static bool ReportsBuildSha(Endpoint? endpoint) =>
        endpoint?.Metadata.GetMetadata<BuildShaHeaderMetadata>() is not null;
}
