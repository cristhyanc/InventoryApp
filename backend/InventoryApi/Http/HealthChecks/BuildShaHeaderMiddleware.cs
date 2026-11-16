namespace InventoryApi.Http.HealthChecks;

/// <summary>
/// Issue #1308 (audit OPS-04, #1228/#1231): puts the build's commit on the two health endpoints'
/// responses in an <c>X-Build-Sha</c> header, so an operator can tell a reply from the old process
/// finishing its restart apart from the new release instead of trusting the first 200/503. The
/// value is <see cref="BuildMetadata.BuildSha"/>, read from the assembly once.
///
/// <para>It is its own middleware rather than a lambda in <c>Program.cs</c> for the same reason
/// every other middleware in this pipeline is: the composition root registers the pipeline, it does
/// not implement request behaviour. The path test and the header name live here, next to the
/// <see cref="BuildMetadata"/> they belong to, where
/// <c>HealthEndpointsTests</c>/<c>HealthEndpointsDatabaseUnavailableTests</c> pin them on both
/// endpoints and on a 503.</para>
///
/// <para><strong>It only adds a header.</strong> Every request passes through to <c>_next</c>
/// untouched, and a request for anything other than the two health paths leaves on one path
/// comparison: nothing here reads or writes a status code, a body, or anything else about the
/// request, and neither endpoint's <c>AllowAnonymous()</c> is affected. Nothing but the commit SHA
/// (or <c>"unknown"</c>) is exposed - no environment name, machine name, configuration or
/// dependency version.</para>
/// </summary>
public sealed class BuildShaHeaderMiddleware
{
    internal const string HeaderName = "X-Build-Sha";

    /// <summary>
    /// The two paths that carry the header, matched exactly and case-sensitively, mirroring the
    /// <c>MapHealthChecks</c> registrations in <c>Program.cs</c>. Scoped to exactly these two so no
    /// other response gains a header, which is what makes this safe to run ahead of every endpoint.
    /// </summary>
    private const string LivenessPath = "/health/live";
    private const string ReadinessPath = "/health/ready";

    private readonly RequestDelegate _next;

    public BuildShaHeaderMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (IsHealthEndpoint(context.Request.Path))
        {
            context.Response.Headers[HeaderName] = BuildMetadata.BuildSha;
        }

        await _next(context);
    }

    private static bool IsHealthEndpoint(PathString path) =>
        path.Equals(LivenessPath, StringComparison.Ordinal) ||
        path.Equals(ReadinessPath, StringComparison.Ordinal);
}
