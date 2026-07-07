using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection.PortableExecutable;

namespace InventoryApi.Integrations.Nayax;

public interface INayaxLynxClient
{
    Task<List<NayaxDevice>> GetDevicesAsync(CancellationToken ct = default);
    Task<List<NayaxMachine>> GetMachinesAsync(CancellationToken ct = default);
    Task<List<NayaxMachineProduct>> GetMachineProductsAsync(int machineId, CancellationToken ct = default);
    Task<List<NayaxMachineProduct>> CreateMachineProductsAsync(int machineId, List<NayaxMachineProduct> products, CancellationToken ct = default);
    Task<List<NayaxProduct>> GetProductsAsync(CancellationToken ct = default);
    Task<List<NayaxProductGroup>> GetProductGroupssAsync(CancellationToken ct = default);
}

// Thin wrapper around Nayax Lynx's REST API.
// Docs: https://devzone.nayax.com/docs/manage-data-operations/lynx-api
// Auth: Bearer token issued via Nayax Core (see NayaxLynxOptions.AccessToken).
public class NayaxLynxClient : INayaxLynxClient
{
    private readonly HttpClient _http;
    private readonly string operatorId;
    public NayaxLynxClient(HttpClient http, IOptions<NayaxLynxOptions> options)
    {
        _http = http;
        var opts = options.Value;

        operatorId = opts.OperatorId;
        _http.BaseAddress = new Uri(opts.BaseUrl.TrimEnd('/') + "/operational/v1/");
        _http.DefaultRequestHeaders.Accept.Clear();
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (!string.IsNullOrWhiteSpace(opts.AccessToken))
        {
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", opts.AccessToken);
        }
    }

    public async Task<List<NayaxDevice>> GetDevicesAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync("devices", ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<NayaxDevice>>(cancellationToken: ct) ?? new();
    }

    public async Task<List<NayaxMachine>> GetMachinesAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync("machines", ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<NayaxMachine>>(cancellationToken: ct) ?? new();
    }

    public async Task<List<NayaxProductGroup>> GetProductGroupssAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"operators/{operatorId}/productGroups", ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<NayaxProductGroup>>(cancellationToken: ct) ?? new();
    }

    public async Task<List<NayaxProduct>> GetProductsAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"operators/{operatorId}/products", ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<NayaxProduct>>(cancellationToken: ct) ?? new();
    }

    public async Task<List<NayaxMachineProduct>> GetMachineProductsAsync(int machineId, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"machines/{machineId}/machineProducts", ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<NayaxMachineProduct>>(cancellationToken: ct) ?? new();
    }

    public async Task<List<NayaxMachineProduct>> CreateMachineProductsAsync(
        int machineId, List<NayaxMachineProduct> products, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync($"machines/{machineId}/machineProducts", products, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<NayaxMachineProduct>>(cancellationToken: ct) ?? new();
    }
}
