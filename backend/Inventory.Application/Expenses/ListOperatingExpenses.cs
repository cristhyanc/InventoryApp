namespace Inventory.Application.Expenses;

public sealed class ListOperatingExpenses
{
    private readonly IOperatingExpenseStore _store;

    public ListOperatingExpenses(IOperatingExpenseStore store)
    {
        _store = store;
    }

    public Task<IReadOnlyList<OperatingExpenseListItem>> Handle(OperatingExpenseFilter filter, CancellationToken cancellationToken) =>
        _store.ListAsync(filter, cancellationToken);
}
