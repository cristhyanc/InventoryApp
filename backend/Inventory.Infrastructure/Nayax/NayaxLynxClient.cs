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
public class NayaxLynxClient : INayaxLynxClient
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

    // The single place a Nayax request is authenticated, sent and accepted or rejected.
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
        var path = endpoint(credential.OperatorId);

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

        _logger.LogError(
            "Nayax request failed. Operation={NayaxOperation} Method={NayaxHttpMethod} Endpoint={NayaxEndpoint} Status={NayaxStatusCode} Reason={NayaxReasonPhrase}",
            operation,
            method.Method,
            endpoint,
            (int)response.StatusCode,
            response.ReasonPhrase);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            // The revision read when this call started, never whatever is stored now: the store's
            // conditional update is what discards this result if the token has since been replaced.
            await _credentials
                .ReportUnauthorizedAsync(credential.CredentialRevision, ct)
                .ConfigureAwait(false);

            throw new NayaxNotConnectedException(NayaxConnectionStatus.NeedsAttention);
        }

        if (response.StatusCode == HttpStatusCode.Forbidden && credential.PermissionsAreUnverified)
        {
            throw new NayaxPermissionNotGrantedException(operation);
        }

        throw new NayaxUpstreamException(operation, method, endpoint, response.StatusCode);
    }
}
