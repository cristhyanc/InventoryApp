namespace InventoryApi.Auth.E2ETesting;

/// <summary>
/// The one gate that decides whether this process is the dedicated end-to-end testing host
/// (issue #46).
///
/// Everything test-only in this folder - the synthetic authentication scheme and the fixture
/// data it authenticates against - is reachable only through this predicate, and it answers
/// <c>true</c> for exactly one hosting environment name. The decision is made from the host
/// environment the process was started with, never from anything a caller can send: no header,
/// query string, route value, cookie, or request body takes part in it, and there is no endpoint
/// that turns it on.
///
/// It fails closed. Production, Development, the <c>Testing</c> environment the backend test
/// suite uses, and any other name all answer <c>false</c>, which leaves the real Microsoft Entra
/// <c>JwtBearer</c> scheme as the only way to authenticate (see
/// <see cref="InventoryApiAuthenticationExtensions"/>). The comparison is ordinal and
/// case-sensitive on purpose: a near-miss such as <c>e2etest</c> is not this environment, and the
/// safe answer to "did someone mean to run the E2E host?" is no.
/// </summary>
public static class E2ETestEnvironment
{
    /// <summary>
    /// The <c>ASPNETCORE_ENVIRONMENT</c> value of the dedicated E2E host. It is deliberately not
    /// a configurable setting: a configuration key could be set on a deployed App Service, while
    /// the environment name is the identity of the host itself.
    /// </summary>
    public const string EnvironmentName = "E2ETest";

    public static bool IsEnabled(IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        return string.Equals(environment.EnvironmentName, EnvironmentName, StringComparison.Ordinal);
    }
}
