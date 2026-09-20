namespace InventoryApi.DTOs;

public sealed record NayaxFeeRateRequest(DateTime EffectiveFrom, decimal FeeExGst);

public sealed record NayaxFeeRateResponse(int Id, DateTime EffectiveFrom, decimal FeeExGst, DateTime CreatedAt);
