namespace Inventory.Application.NayaxFeeSettings;

public sealed class ListNayaxFeeRates
{
    private readonly INayaxFeeRateStore _store;

    public ListNayaxFeeRates(INayaxFeeRateStore store)
    {
        _store = store;
    }

    public Task<IReadOnlyList<NayaxFeeRateRecord>> Handle(CancellationToken cancellationToken) =>
        _store.ListOrderedByEffectiveDateDescendingAsync(cancellationToken);
}
