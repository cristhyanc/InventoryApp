using Inventory.Application.Expenses;
using Inventory.Application.Documents;
using Inventory.Application.Machines;
using Inventory.Domain.Expenses;
using InventoryApi.Tests.Application.Machines;
using Xunit;

namespace InventoryApi.Tests.Application.Expenses;

public class CreateOperatingExpenseTests
{
    private static OperatingExpenseFields Fields(
        string description = "Monthly insurance",
        decimal amountExGst = 10m,
        decimal gstAmount = 1m,
        decimal totalAmount = 11m,
        DateTime? servicePeriodStart = null,
        DateTime? servicePeriodEnd = null,
        long? machineId = null) => new(
        new DateTime(2026, 3, 1), ExpenseCategory.Insurance, description, amountExGst, gstAmount, totalAmount,
        null, null, machineId, servicePeriodStart, servicePeriodEnd, null);

    private static ExpenseAttachmentInput AttachmentInput(string fileName, byte[]? bytes = null) =>
        new(fileName, (bytes ?? [1, 2, 3]).Length, () => new MemoryStream(bytes ?? [1, 2, 3]));

    [Fact]
    public async Task Invalid_fields_return_validation_error_without_touching_store_or_documents()
    {
        var store = new FakeOperatingExpenseStore();
        var documents = new FakeDocumentStorage();
        var useCase = new CreateOperatingExpense(store, documents, new FakeLocalMachineStore());

        var result = await useCase.Handle(Fields(description: ""), null, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal(OperatingExpenseDetails.DescriptionRequiredMessage, result.ValidationError);
        Assert.Empty(documents.SavedFileNames);
    }

    [Fact]
    public async Task Create_without_attachment_persists_fields_and_leaves_documents_untouched()
    {
        var store = new FakeOperatingExpenseStore();
        var documents = new FakeDocumentStorage();
        var useCase = new CreateOperatingExpense(store, documents, new FakeLocalMachineStore());

        var result = await useCase.Handle(Fields(description: "  Monthly insurance  "), null, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal("Monthly insurance", result.Record!.Description);
        Assert.Null(result.Record.AttachmentStoredFileName);
        Assert.Empty(documents.SavedFileNames);
    }

    [Fact]
    public async Task Oversized_attachment_returns_validation_error_without_saving_or_persisting()
    {
        var store = new FakeOperatingExpenseStore();
        var documents = new FakeDocumentStorage();
        var useCase = new CreateOperatingExpense(store, documents, new FakeLocalMachineStore());
        var attachment = AttachmentInput("invoice.pdf", new byte[ExpenseAttachmentPolicy.MaxFileSizeBytes + 1]);

        var result = await useCase.Handle(Fields(), attachment, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal(ExpenseAttachmentPolicy.SizeErrorMessage, result.ValidationError);
        Assert.Empty(documents.SavedFileNames);
    }

    [Fact]
    public async Task Disallowed_extension_returns_validation_error_without_saving_or_persisting()
    {
        var store = new FakeOperatingExpenseStore();
        var documents = new FakeDocumentStorage();
        var useCase = new CreateOperatingExpense(store, documents, new FakeLocalMachineStore());

        var result = await useCase.Handle(Fields(), AttachmentInput("invoice.exe"), CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal(ExpenseAttachmentPolicy.ExtensionErrorMessage, result.ValidationError);
        Assert.Empty(documents.SavedFileNames);
    }

    [Fact]
    public async Task Valid_attachment_is_saved_and_its_metadata_persisted()
    {
        var store = new FakeOperatingExpenseStore();
        var documents = new FakeDocumentStorage();
        var useCase = new CreateOperatingExpense(store, documents, new FakeLocalMachineStore());

        var result = await useCase.Handle(Fields(), AttachmentInput("invoice.pdf"), CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal("invoice.pdf", result.Record!.AttachmentFileName);
        Assert.Equal("application/pdf", result.Record.AttachmentContentType);
        Assert.Equal(3, result.Record.AttachmentFileSizeBytes);
        Assert.NotNull(result.Record.AttachmentStoredFileName);
        Assert.Single(documents.SavedFileNames);
        Assert.True(documents.Contains(DocumentCategory.ExpenseAttachment, result.Record.AttachmentStoredFileName!));
    }

    [Fact]
    public async Task When_persistence_fails_after_saving_the_document_is_cleaned_up()
    {
        var store = new FakeOperatingExpenseStore { ThrowOnAdd = true };
        var documents = new FakeDocumentStorage();
        var useCase = new CreateOperatingExpense(store, documents, new FakeLocalMachineStore());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => useCase.Handle(Fields(), AttachmentInput("invoice.pdf"), CancellationToken.None));

        Assert.Single(documents.SavedFileNames);
        var savedFileName = documents.SavedFileNames[0];
        Assert.Contains(savedFileName, documents.DeletedFileNames);
        Assert.False(documents.Contains(DocumentCategory.ExpenseAttachment, savedFileName));
    }

    /// <summary>
    /// Since issue #780 the expense's machine is this business's own machine row, so it is stored
    /// as submitted - no provider number is involved and nothing translates it.
    /// </summary>
    [Fact]
    public async Task A_machine_of_this_business_is_stored_as_the_local_machine_id()
    {
        var store = new FakeOperatingExpenseStore();
        var documents = new FakeDocumentStorage();
        var machines = new FakeLocalMachineStore([new LocalMachineRecord(41, "Lobby", IsActive: true)]);
        var useCase = new CreateOperatingExpense(store, documents, machines);

        var result = await useCase.Handle(Fields(machineId: 41), null, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(41, result.Record!.MachineId);
    }

    /// <summary>
    /// A machine id that is not one of this business's machines - another business's machine, or
    /// one that no longer exists, or a provider machine number submitted by a stale client - is a
    /// clear refusal, and nothing is written: not the expense, and not the attachment that came
    /// with it. The machine store reads through the tenant filters, so "another business's" and
    /// "not there" are the same answer.
    /// </summary>
    [Fact]
    public async Task A_machine_that_is_not_this_businesss_is_rejected_and_nothing_is_written()
    {
        var store = new FakeOperatingExpenseStore();
        var documents = new FakeDocumentStorage();
        var machines = new FakeLocalMachineStore([new LocalMachineRecord(41, "Lobby", IsActive: true)]);
        var useCase = new CreateOperatingExpense(store, documents, machines);

        var result = await useCase.Handle(
            Fields(machineId: 42), AttachmentInput("invoice.pdf"), CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal(OperatingExpenseDetails.UnknownMachineMessage, result.ValidationError);
        Assert.Empty(documents.SavedFileNames);
        Assert.Empty(await new ListOperatingExpenses(store).Handle(
            new OperatingExpenseFilter(null, null, null, null, null, null), CancellationToken.None));
    }

    /// <summary>
    /// A deactivated machine still takes expenses: it is a machine the business operated when the
    /// cost was incurred, and the recorded fact is not undone by retiring the machine.
    /// </summary>
    [Fact]
    public async Task A_deactivated_machine_of_this_business_is_accepted()
    {
        var store = new FakeOperatingExpenseStore();
        var documents = new FakeDocumentStorage();
        var machines = new FakeLocalMachineStore([new LocalMachineRecord(41, "Retired", IsActive: false)]);
        var useCase = new CreateOperatingExpense(store, documents, machines);

        var result = await useCase.Handle(Fields(machineId: 41), null, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(41, result.Record!.MachineId);
    }
}
