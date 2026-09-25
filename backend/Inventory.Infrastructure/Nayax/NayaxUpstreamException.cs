using System.Net;

namespace Inventory.Infrastructure.Nayax;

// Raised when Nayax Lynx answers with a non-success HTTP status. Lives in Infrastructure, not
// alongside the INayaxLynxClient port in Inventory.Application, because it carries HTTP-specific
// diagnostics (HttpMethod, relative endpoint) that Application must not depend on - see
// CleanArchitectureDependencyTests.Application_must_not_depend_on_EF_Core_or_HTTP_clients.
// Carries only safe diagnostics: the operation, the HTTP method, the relative
// endpoint this application built, and the upstream status code. It must never
// carry the bearer token, an authorization header, or any part of the upstream
// response body.
public sealed class NayaxUpstreamException : Exception
{
    public NayaxUpstreamException(
        string operation,
        HttpMethod method,
        string endpoint,
        HttpStatusCode statusCode,
        Exception? innerException = null)
        : base(
            $"Nayax operation '{operation}' failed: {method.Method} {endpoint} returned {(int)statusCode}.",
            innerException)
    {
        Operation = operation;
        Method = method.Method;
        Endpoint = endpoint;
        StatusCode = statusCode;
    }

    // Stable name for the client call, e.g. "GetMachines".
    public string Operation { get; }

    public string Method { get; }

    // Relative endpoint built by this application, e.g. "machines/42/lastSales".
    public string Endpoint { get; }

    public HttpStatusCode StatusCode { get; }

    public int StatusCodeValue => (int)StatusCode;
}
