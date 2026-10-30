using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Inventory.Application.Nayax;
using Inventory.Domain.Nayax;
using Microsoft.Extensions.Logging;

namespace Inventory.Infrastructure.Nayax;

// Thin wrapper around Nayax Lynx's REST API.
// Docs: https://devzone.nayax.com/docs/manage-data-operations/lynx-api
// Auth: a Bearer token, resolved per call from the current business's own stored connection
// (issue #520) through INayaxRequestCredentialProvider. Only the base URL is global.
public class NayaxLynxClient : INayaxLynxCachedReadClient, INayaxFleetProvenanceClient
{
    private readonly HttpClient _http;
    private readonly INayaxRequestCredentialProvider _credentials;
    private readonly ILogger<NayaxLynxClient> _logger;

    public NayaxLynxClient(
        HttpClient http,
        INayaxRequestCredentialProvider credentials,
        ILogger<NayaxLynxClient> logger)
    {
        _http = http;
        _credentials = credentials;
        _logger = logger;
    }

    public Task<List<NayaxDevice>> GetDevicesAsync(CancellationToken ct = default) =>
        SendAsync<List<NayaxDevice>>(nameof(GetDevicesAsync), HttpMethod.Get, _ => "devices", ct);

    public Task<List<NayaxMachine>> GetMachinesAsync(CancellationToken ct = default) =>
        SendAsync<List<NayaxMachine>>(nameof(GetMachinesAsync), HttpMethod.Get, _ => "machines", ct);

    // This client caches nothing, so "fresh" and "cached" are the same request here: the caches of
    // issues #840 and #842 live in CachingNayaxLynxClient, which is what the composition root
    // registers the port as. Keeping the two identical here is the point - the connection test
    // runner builds this class directly and must never read or write a cache.
    public Task<List<NayaxMachine>> GetMachinesFreshAsync(CancellationToken ct = default) =>
        GetMachinesAsync(ct);

    /// <inheritdoc />
    public async Task<NayaxFleetReadResult> ReadFleetFreshAsync(CancellationToken ct = default)
    {
        // One credential resolution, and it is the one the request is sent with - so the series this
        // answer is attributed to is the series of the account that actually authenticated it
        // (issue #978). Resolving here rather than letting SendAsync resolve its own is what makes
        // the request and its provenance one operation; it is exactly what the machine-list cache
        // does for its key, and it is still one connection read per Nayax call.
        var credential = await _credentials.GetForOperationAsync(ct).ConfigureAwait(false);

        var machines = await SendWithCredentialAsync<List<NayaxMachine>>(
                nameof(GetMachinesAsync), HttpMethod.Get, _ => "machines", credential, ct)
            .ConfigureAwait(false);

        return new NayaxFleetReadResult(machines, credential.AccountSeries);
    }

    /// <inheritdoc />
    public Task<List<NayaxMachine>> ReadMachinesAsync(
        NayaxRequestCredential credential, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credential);

        // The same request as GetMachinesAsync in every respect - same endpoint, same operation name
        // in logs and exceptions, same classification of a 401 or a 403 - except that the credential
        // was resolved and gated by the caller instead of here, so the revision this request carries
        // is the revision the machine-list cache keys its answer under (issue #840).
        return SendWithCredentialAsync<List<NayaxMachine>>(
            nameof(GetMachinesAsync), HttpMethod.Get, _ => "machines", credential, ct);
    }

    public Task<List<NayaxProductGroup>> GetProductGroupssAsync(CancellationToken ct = default) =>
        SendAsync<List<NayaxProductGroup>>(
            nameof(GetProductGroupssAsync),
            HttpMethod.Get,
            operatorId => $"operators/{operatorId}/productGroups",
            ct);

    public Task<List<NayaxProduct>> GetProductsAsync(CancellationToken ct = default) =>
        SendAsync<List<NayaxProduct>>(
            nameof(GetProductsAsync),
            HttpMethod.Get,
            operatorId => $"operators/{operatorId}/products",
            ct);

    public Task<List<NayaxMachineProduct>> GetMachineProductsAsync(
        long machineId, CancellationToken ct = default) =>
        SendAsync<List<NayaxMachineProduct>>(
            nameof(GetMachineProductsAsync),
            HttpMethod.Get,
            _ => $"machines/{machineId}/machineProducts",
            ct);

    // Identical to GetMachineProductsAsync, for the same reason the two machine-list members are
    // identical here: the 60-second selections cache of issue #842 lives in CachingNayaxLynxClient,
    // and this class is what the connection test builds over pasted candidate credentials.
    public Task<List<NayaxMachineProduct>> GetMachineProductsFreshAsync(
        long machineId, CancellationToken ct = default) =>
        GetMachineProductsAsync(machineId, ct);

    /// <inheritdoc />
    public Task<List<NayaxMachineProduct>> ReadMachineProductsAsync(
        long machineId, NayaxRequestCredential credential, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credential);

        // The same request as GetMachineProductsAsync in every respect - same endpoint, same
        // operation name in logs and exceptions, same classification of a 401 or a 403 - except that
        // the credential was resolved and gated by the caller instead of here, so the revision this
        // request carries is the revision the selections cache keys its answer under (issue #842).
        return SendWithCredentialAsync<List<NayaxMachineProduct>>(
            nameof(GetMachineProductsAsync),
            HttpMethod.Get,
            _ => $"machines/{machineId}/machineProducts",
            credential,
            ct);
    }

    public Task<NayaxMachine> GetMachineAsync(long machineId, CancellationToken ct = default) =>
        SendAsync<NayaxMachine>(
            nameof(GetMachineAsync),
            HttpMethod.Get,
            _ => $"machines/{machineId}/",
            ct);

    public Task<List<NayaxLastSalesReport>> GetMachineLastSalesAsync(
        long machineId, CancellationToken ct = default) =>
        SendAsync<List<NayaxLastSalesReport>>(
            nameof(GetMachineLastSalesAsync),
            HttpMethod.Get,
            _ => $"machines/{machineId}/lastSales",
            ct);

    public Task<List<NayaxMachineAlert>> GetMachineLastAlertsAsync(
        long machineId, CancellationToken ct = default) =>
        SendAsync<List<NayaxMachineAlert>>(
            nameof(GetMachineLastAlertsAsync),
            HttpMethod.Get,
            _ => $"machines/{machineId}/lastAlerts",
            ct);

    public Task<List<NayaxMachineProduct>> CreateMachineProductsAsync(
        long machineId, List<NayaxMachineProduct> products, CancellationToken ct = default) =>
        SendAsync<List<NayaxMachineProduct>>(
            nameof(CreateMachineProductsAsync),
            HttpMethod.Post,
            _ => $"machines/{machineId}/machineProducts",
            ct,
            () => JsonContent.Create(products));

    /// <inheritdoc />
    public Task<List<NayaxMachineProduct>> WriteMachineProductsAsync(
        long machineId,
        List<NayaxMachineProduct> products,
        NayaxRequestCredential credential,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credential);

        // The same request as CreateMachineProductsAsync, with the caller's already-gated credential:
        // the entry the caller then forgets has to belong to the credentials this write really
        // carried, and resolving a second one here would cost the business a second connection read
        // per write (issue #842).
        return SendWithCredentialAsync<List<NayaxMachineProduct>>(
            nameof(CreateMachineProductsAsync),
            HttpMethod.Post,
            _ => $"machines/{machineId}/machineProducts",
            credential,
            ct,
            () => JsonContent.Create(products));
    }

    // One Nayax operation: resolve this call's credential, then send, accept or reject.
    //
    // The credential is resolved first, per call, and is applied to this request only: nothing is
    // baked into the shared HttpClient, so a token can never outlive the business whose request
    // fetched it. The operator id comes from the same credential, which is why the endpoint is
    // built from it rather than passed in - the operator path parameter and the bearer token have
    // to belong to the same business, always.
    private async Task<TResponse> SendAsync<TResponse>(
        string operation,
        HttpMethod method,
        Func<string, string> endpoint,
        CancellationToken ct,
        Func<HttpContent>? content = null)
        where TResponse : new()
    {
        var credential = await _credentials.GetForOperationAsync(ct).ConfigureAwait(false);

        return await SendWithCredentialAsync<TResponse>(operation, method, endpoint, credential, ct, content)
            .ConfigureAwait(false);
    }

    // The single place a Nayax request is authenticated, sent and accepted or rejected, for every
    // operation and whoever resolved its credential.
    //
    // It is separate from the resolution above so the one caller that has already resolved and gated a
    // credential of its own - the machine-list cache's decorator, through ReadMachinesAsync - sends
    // the request with exactly that credential rather than with a second snapshot read a moment later,
    // which is what lets the cache key name the credentials the request really carried (issue #840).
    // Every other operation resolves one credential per call and arrives here immediately, which is
    // what it has always done.
    private async Task<TResponse> SendWithCredentialAsync<TResponse>(
        string operation,
        HttpMethod method,
        Func<string, string> endpoint,
        NayaxRequestCredential credential,
        CancellationToken ct,
        Func<HttpContent>? content = null)
        where TResponse : new()
    {
        // The operator id is escaped before it becomes part of a path (issue #681). The value is
        // validated as digits where it enters the application (Inventory.Domain.Nayax.NayaxOperatorId,
        // matching Nayax's own int64 contract for the operator path parameter), so escaping changes
        // nothing for a legitimate id; what it removes is what a row written before that rule, a
        // hand-written UPDATE or a future caller could otherwise do - a `/`, `?`, `#` or `..` in the
        // id does not merely fail, it addresses a *different* Nayax endpoint, and the two
        // operator-scoped connection checks conclude "that operator is yours" from the absence of a
        // 404.
        var path = endpoint(Uri.EscapeDataString(credential.OperatorId));

        using var request = new HttpRequestMessage(method, path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);
        if (content is not null)
        {
            request.Content = content();
        }

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        await EnsureNayaxSuccessAsync(response, credential, operation, method, path, ct).ConfigureAwait(false);

        return await response.Content
            .ReadFromJsonAsync<TResponse>(cancellationToken: ct)
            .ConfigureAwait(false) ?? new();
    }

    // Single place where a Nayax response is accepted or rejected. Successful responses pass
    // straight through; anything else is logged with safe fields only and then classified. The
    // upstream response body is deliberately never read or logged, and an empty collection is never
    // substituted for a failure.
    //
    // Three outcomes, because Nayax's own documented statuses mean three different things
    // (https://devzone.nayax.com/docs/manage-data-operations/lynx-api/app-tokens):
    //
    //   401  the token is invalid or has expired - a verdict on the stored credentials. The
    //        connection moves to NeedsAttention for the revision this call started with, and the
    //        caller gets the stable "not connected" error, because that is now its state.
    //   403  the token's scopes do not cover this resource or action - a verdict on one feature.
    //        While the credentials have not been tested since they were saved, that is the
    //        per-feature "permission not granted" answer and nothing writes a status: the rest of
    //        the integration may be working perfectly. A 403 against credentials that were tested
    //        and worked stays an ordinary upstream failure (the behaviour that predates issue #520).
    //   else  an ordinary upstream failure, surfaced as NayaxUpstreamException for the HTTP
    //        boundary's controlled 502, exactly as before.
    private async Task EnsureNayaxSuccessAsync(
        HttpResponseMessage response,
        NayaxRequestCredential credential,
        string operation,
        HttpMethod method,
        string endpoint,
        CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        // A cancelled caller must keep observing cancellation rather than a 502 - or a status write.
        ct.ThrowIfCancellationRequested();

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            // The revision read when this call started, never whatever is stored now: the store's
            // conditional update is what discards this result if the token has since been replaced.
            await _credentials
                .ReportUnauthorizedAsync(credential.CredentialRevision, ct)
                .ConfigureAwait(false);

            // NayaxConnectionExceptionHandler logs this once, at Warning: a connection failure is an
            // expected, business-facing outcome, not a server error.
            throw new NayaxNotConnectedException(NayaxConnectionStatus.NeedsAttention);
        }

        if (response.StatusCode == HttpStatusCode.Forbidden && credential.PermissionsAreUnverified)
        {
            // Same reasoning as the 401 branch above: logged once, at Warning, by the handler.
            throw new NayaxPermissionNotGrantedException(operation);
        }

        _logger.LogError(
            "Nayax request failed. Operation={NayaxOperation} Method={NayaxHttpMethod} Endpoint={NayaxEndpoint} Status={NayaxStatusCode} Reason={NayaxReasonPhrase}",
            operation,
            method.Method,
            endpoint,
            (int)response.StatusCode,
            response.ReasonPhrase);

        throw new NayaxUpstreamException(operation, method, endpoint, response.StatusCode);
    }
}
