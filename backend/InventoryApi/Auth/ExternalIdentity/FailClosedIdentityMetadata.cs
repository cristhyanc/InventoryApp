using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace InventoryApi.Auth.ExternalIdentity;

/// <summary>
/// Turns an unreadable identity metadata document into a refused token rather than a failed
/// request (issue #710, design § 9.3 and § 11, T10).
///
/// <para>
/// The bearer handler reads the issuer and the signing keys from the authority's OpenID Connect
/// metadata document on each request. When that read fails - the authority is unreachable, its
/// name does not resolve, a key refresh fails, or the configured host is not an identity authority
/// at all - the exception leaves the handler and the request ends as a server error, which tells a
/// caller that something here is broken rather than that their credential could not be validated.
/// </para>
///
/// <para>
/// This wrapper answers with a metadata document that names no issuer and publishes no key
/// instead, so validation takes the ordinary path and fails: the token is <c>401</c>, as a token
/// that cannot be validated must be. Nothing degrades - there is no branch here that skips the
/// issuer check, trusts an unverified signature, accepts a stale key set or falls back to another
/// authority - and the real cause is logged as an error so an outage is visible in telemetry
/// rather than only in a status code. Refreshing is still the inner manager's decision; this never
/// caches an outcome of its own.
/// </para>
/// </summary>
internal sealed class FailClosedIdentityMetadata : IConfigurationManager<OpenIdConnectConfiguration>
{
    private readonly IConfigurationManager<OpenIdConnectConfiguration> _authority;
    private readonly ILogger _logger;

    public FailClosedIdentityMetadata(IConfigurationManager<OpenIdConnectConfiguration> authority, ILogger logger)
    {
        _authority = authority;
        _logger = logger;
    }

    public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel)
    {
        try
        {
            return await _authority.GetConfigurationAsync(cancel);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(
                exception,
                "The customer identity authority's metadata document could not be read, so every bearer token from that authority is rejected. Workforce Microsoft sign-in is unaffected.");

            // A fresh document each time: nothing may share, mutate or cache this state.
            return new OpenIdConnectConfiguration();
        }
    }

    public void RequestRefresh() => _authority.RequestRefresh();
}
