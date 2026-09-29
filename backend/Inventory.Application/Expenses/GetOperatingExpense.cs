namespace Inventory.Application.Expenses;

public sealed class GetOperatingExpense
{
    private readonly IOperatingExpenseStore _store;

    public GetOperatingExpense(IOperatingExpenseStore store)
    {
        _store = store;
    }

    public Task<OperatingExpenseRecord?> Handle(int id, CancellationToken cancellationToken) =>
        _store.FindByIdAsync(id, cancellationToken);
}
