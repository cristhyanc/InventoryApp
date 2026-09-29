using Inventory.Application.Expenses;
using Inventory.Domain.Expenses;
using Xunit;

namespace InventoryApi.Tests.Application.Expenses;

public class GetOperatingExpenseTests
{
    [Fact]
    public async Task Missing_expense_returns_null()
    {
        var store = new FakeOperatingExpenseStore();

        var result = await new GetOperatingExpense(store).Handle(999, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task Existing_expense_is_returned()
    {
        var store = new FakeOperatingExpenseStore();
        var documents = new FakeDocumentStorage();
        var fields = new OperatingExpenseFields(
            new DateTime(2026, 3, 1), ExpenseCategory.Insurance, "Insurance", 10m, 1m, 11m,
            null, null, null, null, null, null);
        var created = await new CreateOperatingExpense(store, documents).Handle(fields, null, CancellationToken.None);

        var result = await new GetOperatingExpense(store).Handle(created.Record!.Id, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Insurance", result!.Description);
    }
}
