namespace Inventory.Application.NayaxFeeSettings;

/// <summary>
/// Narrow persistence port for Nayax processing fee rates, owned by the Application layer.
/// </summary>
public interface INayaxFeeRateStore
{
    Task<IReadOnlyList<NayaxFeeRateRecord>> ListOrderedByEffectiveDateDescendingAsync(CancellationToken cancellationToken);

    Task<NayaxFeeRateRecord?> FindByEffectiveDateAsync(DateTime effectiveDate, CancellationToken cancellationToken);

    Task<NayaxFeeRateRecord> AddAsync(DateTime effectiveDate, decimal feeExGst, DateTime createdAtUtc, CancellationToken cancellationToken);

    Task<NayaxFeeRateRecord> UpdateFeeAsync(int id, decimal feeExGst, CancellationToken cancellationToken);
}
