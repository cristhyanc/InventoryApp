using System.Net.Http.Headers;
using System.Net.Http.Json;
using Inventory.Application.Nayax;
using Microsoft.Extensions.Logging;

namespace Inventory.Infrastructure.Nayax;

// Thin wrapper around Nayax Lynx's REST API.
// Docs: https://devzone.nayax.com/docs/manage-data-operations/lynx-api
// Auth: Bearer token issued via Nayax Core (see NayaxLynxOptions.AccessToken).
public class NayaxLynxClient : INayaxLynxClient
{
    private readonly HttpClient _http;
    private readonly string operatorId;
    private readonly ILogger<NayaxLynxClient> _logger;

    public NayaxLynxClient(HttpClient http, NayaxLynxOptions options, ILogger<NayaxLynxClient> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _http = http;
        _logger = logger;
        operatorId = options.OperatorId;
        _http.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/operational/v1/");
        _http.DefaultRequestHeaders.Accept.Clear();
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(options.AccessToken))
        {
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", options.AccessToken);
        }
    }

    public async Task<List<NayaxDevice>> GetDevicesAsync(CancellationToken ct = default)
    {
        const string endpoint = "devices";
        var response = await _http.GetAsync(endpoint, ct);
        EnsureNayaxSuccess(response, nameof(GetDevicesAsync), HttpMethod.Get, endpoint, ct);
        return await response.Content.ReadFromJsonAsync<List<NayaxDevice>>(cancellationToken: ct) ?? new();
    }

    public async Task<List<NayaxMachine>> GetMachinesAsync(CancellationToken ct = default)
    {
        const string endpoint = "machines";
        var response = await _http.GetAsync(endpoint, ct);
        EnsureNayaxSuccess(response, nameof(GetMachinesAsync), HttpMethod.Get, endpoint, ct);
        return await response.Content.ReadFromJsonAsync<List<NayaxMachine>>(cancellationToken: ct) ?? new();
    }

    public async Task<List<NayaxProductGroup>> GetProductGroupssAsync(CancellationToken ct = default)
    {
        var endpoint = $"operators/{operatorId}/productGroups";
        var response = await _http.GetAsync(endpoint, ct);
        EnsureNayaxSuccess(response, nameof(GetProductGroupssAsync), HttpMethod.Get, endpoint, ct);
        return await response.Content.ReadFromJsonAsync<List<NayaxProductGroup>>(cancellationToken: ct) ?? new();
    }

    public async Task<List<NayaxProduct>> GetProductsAsync(CancellationToken ct = default)
    {
        var endpoint = $"operators/{operatorId}/products";
        var response = await _http.GetAsync(endpoint, ct);
        EnsureNayaxSuccess(response, nameof(GetProductsAsync), HttpMethod.Get, endpoint, ct);
        return await response.Content.ReadFromJsonAsync<List<NayaxProduct>>(cancellationToken: ct) ?? new();
    }

    public async Task<List<NayaxMachineProduct>> GetMachineProductsAsync(long machineId, CancellationToken ct = default)
    {
        var endpoint = $"machines/{machineId}/machineProducts";
        var response = await _http.GetAsync(endpoint, ct);
        EnsureNayaxSuccess(response, nameof(GetMachineProductsAsync), HttpMethod.Get, endpoint, ct);
        return await response.Content.ReadFromJsonAsync<List<NayaxMachineProduct>>(cancellationToken: ct) ?? new();
    }

    public async Task<NayaxMachine> GetMachineAsync(long machineId, CancellationToken ct = default)
    {
        var endpoint = $"machines/{machineId}/";
        var response = await _http.GetAsync(endpoint, ct);
        EnsureNayaxSuccess(response, nameof(GetMachineAsync), HttpMethod.Get, endpoint, ct);
        return await response.Content.ReadFromJsonAsync<NayaxMachine>(cancellationToken: ct) ?? new();
    }

    public async Task<List<NayaxLastSalesReport>> GetMachineLastSalesAsync(long machineId, CancellationToken ct = default)
    {
        var endpoint = $"machines/{machineId}/lastSales";
        var response = await _http.GetAsync(endpoint, ct);
        EnsureNayaxSuccess(response, nameof(GetMachineLastSalesAsync), HttpMethod.Get, endpoint, ct);
        return await response.Content.ReadFromJsonAsync<List<NayaxLastSalesReport>>(cancellationToken: ct) ?? new();
    }

    public async Task<List<NayaxMachineProduct>> CreateMachineProductsAsync(
        long machineId, List<NayaxMachineProduct> products, CancellationToken ct = default)
    {
        var endpoint = $"machines/{machineId}/machineProducts";
        var response = await _http.PostAsJsonAsync(endpoint, products, ct);
        EnsureNayaxSuccess(response, nameof(CreateMachineProductsAsync), HttpMethod.Post, endpoint, ct);
        return await response.Content.ReadFromJsonAsync<List<NayaxMachineProduct>>(cancellationToken: ct) ?? new();
    }

    // Single place where a Nayax response is accepted or rejected. Successful
    // responses pass straight through; anything else is logged with safe fields
    // only and surfaced as NayaxUpstreamException so the HTTP boundary can map it
    // to a controlled 502. The upstream response body is deliberately never read
    // or logged, and an empty collection is never substituted for a failure.
    private void EnsureNayaxSuccess(
        HttpResponseMessage response,
        string operation,
        HttpMethod method,
        string endpoint,
        CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        // A cancelled caller must keep observing cancellation rather than a 502.
        ct.ThrowIfCancellationRequested();

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
