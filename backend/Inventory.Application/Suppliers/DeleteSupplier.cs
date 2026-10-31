namespace Inventory.Application.Suppliers;

public sealed class DeleteSupplier
{
    private readonly ISupplierStore _store;

    public DeleteSupplier(ISupplierStore store)
    {
        _store = store;
    }

    /// <summary>
    /// Deletes a supplier the business created itself. A copy of a platform supplier preset is
    /// refused instead (issue #934) - the store applies
    /// <c>SupplierPresetLinkPolicy</c> to the row it read, so the decision and the delete are one
    /// operation.
    /// </summary>
    public Task<SupplierCommandResult> Handle(int id, CancellationToken cancellationToken) =>
        _store.DeleteAsync(id, cancellationToken);
}
