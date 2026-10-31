namespace Inventory.Application.Suppliers;

/// <summary>
/// Sets or clears a linked supplier's "Not used" switch (issue #934).
///
/// <para>It is its own use case rather than a field on the supplier update, because it is the one
/// thing a business may decide about a copy's <em>presence</em>: the update replaces the details an
/// operator typed, and carrying a hidden flag through that body would let a stale form put a
/// supplier back in the pickers as a side effect of correcting a phone number.</para>
/// </summary>
public sealed class SetSupplierNotUsed
{
    private readonly ISupplierStore _store;

    public SetSupplierNotUsed(ISupplierStore store)
    {
        _store = store;
    }

    /// <summary>
    /// Hides the supplier from the pickers, or shows it again. Switching it to the state it is
    /// already in is accepted and writes nothing new, so a repeated request is not an error.
    /// </summary>
    public Task<SupplierCommandResult> Handle(int id, bool isHidden, CancellationToken cancellationToken) =>
        _store.SetHiddenAsync(id, isHidden, cancellationToken);
}
