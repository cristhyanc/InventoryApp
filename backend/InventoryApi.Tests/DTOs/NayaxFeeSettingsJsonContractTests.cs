using System.Text.Json;
using InventoryApi.DTOs;
using Xunit;

namespace InventoryApi.Tests.DTOs;

/// <summary>
/// Locks the wire contract of the Nayax fee-settings endpoints now that purpose-built DTOs replaced
/// the EF entity. The serializer options are the ones <c>AddControllers()</c> configures by default
/// (<see cref="JsonSerializerDefaults.Web"/>), so these assertions describe the JSON the API really
/// sends and accepts.
/// </summary>
public class NayaxFeeSettingsJsonContractTests
{
    private static readonly JsonSerializerOptions WebDefaults = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Response_serializes_with_exactly_id_effectiveFrom_feeExGst_and_createdAt()
    {
        var response = new NayaxFeeRateResponse(
            7,
            new DateTime(2026, 3, 1),
            0.19m,
            new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc));

        var json = JsonSerializer.Serialize(response, WebDefaults);

        using var document = JsonDocument.Parse(json);
        Assert.Equal(
            new[] { "id", "effectiveFrom", "feeExGst", "createdAt" },
            document.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal(7, document.RootElement.GetProperty("id").GetInt32());
        Assert.Equal(new DateTime(2026, 3, 1), document.RootElement.GetProperty("effectiveFrom").GetDateTime());
        Assert.Equal(0.19m, document.RootElement.GetProperty("feeExGst").GetDecimal());
        Assert.Equal(
            new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
            document.RootElement.GetProperty("createdAt").GetDateTime());
    }

    [Fact]
    public void Request_deserializes_the_existing_post_body()
    {
        const string json = """{"effectiveFrom":"2026-03-01T00:00:00","feeExGst":0.19}""";

        var request = JsonSerializer.Deserialize<NayaxFeeRateRequest>(json, WebDefaults);

        Assert.NotNull(request);
        Assert.Equal(new DateTime(2026, 3, 1), request.EffectiveFrom);
        Assert.Equal(0.19m, request.FeeExGst);
    }

    [Fact]
    public void Request_still_accepts_the_extra_id_and_createdAt_fields_the_entity_contract_allowed()
    {
        const string json = """
            {"id":7,"effectiveFrom":"2026-03-01T09:30:00","feeExGst":0.21,"createdAt":"2026-05-01T12:00:00Z"}
            """;

        var request = JsonSerializer.Deserialize<NayaxFeeRateRequest>(json, WebDefaults);

        Assert.NotNull(request);
        Assert.Equal(new DateTime(2026, 3, 1, 9, 30, 0), request.EffectiveFrom);
        Assert.Equal(0.21m, request.FeeExGst);
    }
}
