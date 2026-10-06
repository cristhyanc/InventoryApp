using Inventory.Application.Expenses;
using Inventory.Domain.Expenses;
using Xunit;

namespace InventoryApi.Tests.Application.Expenses;

public class GetOperatingExpenseAttachmentTests
{
    private static OperatingExpenseFields Fields() => new(
        new DateTime(2026, 3, 1), ExpenseCategory.Insurance, "Insurance", 10m, 1m, 11m,
        null, null, null, null, null, null);

    [Fact]
    public async Task Missing_expense_returns_null()
    {
        var store = new FakeOperatingExpenseStore();
        var documents = new FakeDocumentStorage();

        var result = await new GetOperatingExpenseAttachment(store, documents).Handle(999, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task Expense_without_an_attachment_returns_null()
    {
        var store = new FakeOperatingExpenseStore();
        var documents = new FakeDocumentStorage();
        var created = await new CreateOperatingExpense(store, documents).Handle(Fields(), null, CancellationToken.None);

        var result = await new GetOperatingExpenseAttachment(store, documents).Handle(created.Record!.Id, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task Expense_with_a_stored_attachment_returns_its_content_and_metadata()
    {
        var store = new FakeOperatingExpenseStore();
        var documents = new FakeDocumentStorage();
        var attachment = new ExpenseAttachmentInput("invoice.pdf", 3, () => new MemoryStream([1, 2, 3]));
        var created = await new CreateOperatingExpense(store, documents).Handle(Fields(), attachment, CancellationToken.None);

        var result = await new GetOperatingExpenseAttachment(store, documents).Handle(created.Record!.Id, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("invoice.pdf", result!.FileName);
        Assert.Equal("application/pdf", result.ContentType);
        using var buffer = new MemoryStream();
        await result.Document.Content.CopyToAsync(buffer);
        Assert.Equal(new byte[] { 1, 2, 3 }, buffer.ToArray());
    }
}
