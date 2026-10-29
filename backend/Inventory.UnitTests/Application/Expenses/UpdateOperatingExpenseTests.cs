using Inventory.Application.Documents;
using Inventory.Application.Expenses;
using Inventory.Application.Machines;
using Inventory.Domain.Expenses;
using InventoryApi.Tests.Application.Attachments;
using InventoryApi.Tests.Application.Machines;
using Xunit;

namespace InventoryApi.Tests.Application.Expenses;

public class UpdateOperatingExpenseTests
{
    private static OperatingExpenseFields Fields(
        string description = "Updated insurance", long? machineId = null) => new(
        new DateTime(2026, 3, 1), ExpenseCategory.Insurance, description, 10m, 1m, 11m,
        null, null, machineId, null, null, null);

    private static ExpenseAttachmentInput AttachmentInput(string fileName, byte[]? bytes = null) =>
        new(fileName, (bytes ?? [1, 2, 3]).Length, () => new MemoryStream(bytes ?? [1, 2, 3]));

    private static async Task<(FakeOperatingExpenseStore Store, FakeDocumentStorage Documents, int Id, string StoredFileName)>
        SeedWithAttachmentAsync()
    {
        var store = new FakeOperatingExpenseStore();
        var documents = new FakeDocumentStorage();
        var created = await new CreateOperatingExpense(store, documents, new FakeLocalMachineStore(), FakeAttachmentQuota.NotEnforced())
            .Handle(Fields("Original"), AttachmentInput("old.pdf"), CancellationToken.None);
        return (store, documents, created.Record!.Id, created.Record.AttachmentStoredFileName!);
    }

    [Fact]
    public async Task Missing_expense_returns_not_found_without_touching_documents()
    {
        var store = new FakeOperatingExpenseStore();
        var documents = new FakeDocumentStorage();
        var useCase = new UpdateOperatingExpense(store, documents, new FakeLocalMachineStore(), FakeAttachmentQuota.NotEnforced());

        var result = await useCase.Handle(999, Fields(), null, CancellationToken.None);

        Assert.True(result.IsNotFound);
        Assert.Empty(documents.SavedFileNames);
    }

    [Fact]
    public async Task Missing_expense_returns_not_found_even_when_the_attachment_is_invalid()
    {
        var store = new FakeOperatingExpenseStore();
        var documents = new FakeDocumentStorage();
        var useCase = new UpdateOperatingExpense(store, documents, new FakeLocalMachineStore(), FakeAttachmentQuota.NotEnforced());

        var result = await useCase.Handle(999, Fields(), AttachmentInput("new.exe"), CancellationToken.None);

        // The retired controller resolved the row before validating the attachment, so an unknown
        // id must keep answering not-found rather than the attachment validation error.
        Assert.True(result.IsNotFound);
        Assert.Null(result.ValidationError);
        Assert.Empty(documents.SavedFileNames);
    }

    [Fact]
    public async Task Invalid_fields_return_validation_error_before_any_lookup()
    {
        var store = new FakeOperatingExpenseStore();
        var documents = new FakeDocumentStorage();
        var useCase = new UpdateOperatingExpense(store, documents, new FakeLocalMachineStore(), FakeAttachmentQuota.NotEnforced());

        var result = await useCase.Handle(1, Fields(description: ""), null, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.False(result.IsNotFound);
        Assert.Equal(OperatingExpenseDetails.DescriptionRequiredMessage, result.ValidationError);
    }

    [Fact]
    public async Task Update_without_attachment_leaves_the_existing_attachment_untouched()
    {
        var (store, documents, id, storedFileName) = await SeedWithAttachmentAsync();
        var useCase = new UpdateOperatingExpense(store, documents, new FakeLocalMachineStore(), FakeAttachmentQuota.NotEnforced());

        var result = await useCase.Handle(id, Fields("Updated"), null, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal("Updated", result.Record!.Description);
        Assert.Equal(storedFileName, result.Record.AttachmentStoredFileName);
        Assert.Empty(documents.DeletedFileNames);
    }

    [Fact]
    public async Task Replacing_the_attachment_deletes_the_previous_document_only_after_saving()
    {
        var (store, documents, id, oldStoredFileName) = await SeedWithAttachmentAsync();
        var useCase = new UpdateOperatingExpense(store, documents, new FakeLocalMachineStore(), FakeAttachmentQuota.NotEnforced());

        var result = await useCase.Handle(id, Fields("Updated"), AttachmentInput("new.png"), CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal("new.png", result.Record!.AttachmentFileName);
        Assert.NotEqual(oldStoredFileName, result.Record.AttachmentStoredFileName);
        Assert.True(documents.Contains(DocumentCategory.ExpenseAttachment, result.Record.AttachmentStoredFileName!));
        Assert.False(documents.Contains(DocumentCategory.ExpenseAttachment, oldStoredFileName));
        Assert.Contains(oldStoredFileName, documents.DeletedFileNames);
    }

    [Fact]
    public async Task Failed_replacement_preserves_the_existing_attachment()
    {
        var (store, documents, id, oldStoredFileName) = await SeedWithAttachmentAsync();
        var useCase = new UpdateOperatingExpense(store, documents, new FakeLocalMachineStore(), FakeAttachmentQuota.NotEnforced());

        var result = await useCase.Handle(id, Fields("Updated"), AttachmentInput("new.exe"), CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal(ExpenseAttachmentPolicy.ExtensionErrorMessage, result.ValidationError);
        Assert.True(documents.Contains(DocumentCategory.ExpenseAttachment, oldStoredFileName));
        var stillCurrent = await store.FindByIdAsync(id, CancellationToken.None);
        Assert.Equal(oldStoredFileName, stillCurrent!.AttachmentStoredFileName);
    }

    [Fact]
    public async Task When_persistence_fails_after_saving_the_new_document_is_cleaned_up_and_old_one_kept()
    {
        var (store, documents, id, oldStoredFileName) = await SeedWithAttachmentAsync();
        store.ThrowOnUpdate = true;
        var useCase = new UpdateOperatingExpense(store, documents, new FakeLocalMachineStore(), FakeAttachmentQuota.NotEnforced());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => useCase.Handle(id, Fields("Updated"), AttachmentInput("new.png"), CancellationToken.None));

        Assert.True(documents.Contains(DocumentCategory.ExpenseAttachment, oldStoredFileName));
        var newStoredFileName = Assert.Single(documents.SavedFileNames, name => name != oldStoredFileName);
        Assert.False(documents.Contains(DocumentCategory.ExpenseAttachment, newStoredFileName));
    }

    /// <summary>
    /// Since issue #780 the submitted machine is this business's own machine row, and an edit may
    /// move an expense onto one.
    /// </summary>
    [Fact]
    public async Task An_edit_may_attribute_the_expense_to_a_machine_of_this_business()
    {
        var store = new FakeOperatingExpenseStore();
        var documents = new FakeDocumentStorage();
        var machines = new FakeLocalMachineStore([new LocalMachineRecord(41, "Lobby", IsActive: true)]);
        var created = await new CreateOperatingExpense(store, documents, machines, FakeAttachmentQuota.NotEnforced())
            .Handle(Fields("Original"), null, CancellationToken.None);

        var result = await new UpdateOperatingExpense(store, documents, machines, FakeAttachmentQuota.NotEnforced())
            .Handle(created.Record!.Id, Fields("Updated", machineId: 41), null, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(41, result.Record!.MachineId);
    }

    /// <summary>
    /// A machine that is not this business's is refused with the same message create gives, and the
    /// stored expense keeps the machine, the fields and the attachment it had.
    /// </summary>
    [Fact]
    public async Task A_machine_that_is_not_this_businesss_is_rejected_and_the_expense_is_untouched()
    {
        var (store, documents, id, oldStoredFileName) = await SeedWithAttachmentAsync();
        var machines = new FakeLocalMachineStore([new LocalMachineRecord(41, "Lobby", IsActive: true)]);
        var useCase = new UpdateOperatingExpense(store, documents, machines, FakeAttachmentQuota.NotEnforced());

        var result = await useCase.Handle(
            id, Fields("Updated", machineId: 42), AttachmentInput("new.pdf"), CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal(OperatingExpenseDetails.UnknownMachineMessage, result.ValidationError);
        var unchanged = await store.FindByIdAsync(id, CancellationToken.None);
        Assert.Equal("Original", unchanged!.Description);
        Assert.Null(unchanged.MachineId);
        Assert.Equal(oldStoredFileName, unchanged.AttachmentStoredFileName);
        Assert.Single(documents.SavedFileNames);
    }
}
