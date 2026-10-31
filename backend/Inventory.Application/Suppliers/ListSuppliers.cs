namespace Inventory.Application.Suppliers;

public sealed class ListSuppliers
{
    private readonly ISupplierStore _store;

    public ListSuppliers(ISupplierStore store)
    {
        _store = store;
    }

    /// <summary>
    /// The business's suppliers, ordered by name.
    ///
    /// <para><paramref name="includeHidden"/> decides whether the ones marked "Not used" are among
    /// them (issue #934). It defaults to excluding them, so a caller that says nothing gets the
    /// list a picker needs: a supplier the business has taken out of use must not reappear on a
    /// purchase form because a client forgot a flag. Only the Suppliers page, which has to show a
    /// hidden supplier in order to switch it back on, asks for them.</para>
    /// </summary>
    public Task<IReadOnlyList<SupplierRecord>> Handle(bool includeHidden, CancellationToken cancellationToken) =>
        includeHidden
            ? _store.ListOrderedByNameAsync(cancellationToken)
            : _store.ListSelectableOrderedByNameAsync(cancellationToken);
}
