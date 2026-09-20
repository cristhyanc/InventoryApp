namespace Inventory.Application.NayaxFeeSettings;

/// <summary>
/// A persisted Nayax processing fee rate as seen by the Application layer.
/// </summary>
public sealed record NayaxFeeRateRecord(int Id, DateTime EffectiveFrom, decimal FeeExGst, DateTime CreatedAt);
