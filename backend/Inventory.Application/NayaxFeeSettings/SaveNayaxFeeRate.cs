using Inventory.Application.Time;
using Inventory.Domain.NayaxFeeSettings;

namespace Inventory.Application.NayaxFeeSettings;

public sealed class SaveNayaxFeeRate
{
    private readonly INayaxFeeRateStore _store;
    private readonly IClock _clock;

    public SaveNayaxFeeRate(INayaxFeeRateStore store, IClock clock)
    {
        _store = store;
        _clock = clock;
    }

    public async Task<SaveNayaxFeeRateResult> Handle(decimal feeExGst, DateTime effectiveFrom, CancellationToken cancellationToken)
    {
        if (!NayaxFeeRate.TryCreate(feeExGst, out var rate))
            return SaveNayaxFeeRateResult.Invalid(NayaxFeeRate.ValidationErrorMessage);

        var effectiveDate = effectiveFrom.Date;
        var existing = await _store.FindByEffectiveDateAsync(effectiveDate, cancellationToken);

        var record = existing is null
            ? await _store.AddAsync(effectiveDate, rate.Value, _clock.UtcNow, cancellationToken)
            : await _store.UpdateFeeAsync(existing.Id, rate.Value, cancellationToken);

        return SaveNayaxFeeRateResult.Success(record);
    }
}
