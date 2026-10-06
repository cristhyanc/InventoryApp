using Inventory.Domain.Gst;

namespace Inventory.Application.Suppliers;

/// <summary>
/// Reads one supplier's explicitly configured GST defaults (issue #430). <c>null</c> means the
/// supplier itself is not visible to the caller's business; a visible supplier nobody has
/// configured answers <see cref="SupplierGstDefaults.None"/>.
/// </summary>
public sealed class GetSupplierGstDefaults
{
    private readonly ISupplierStore _store;

    public GetSupplierGstDefaults(ISupplierStore store)
    {
        _store = store;
    }

    public Task<SupplierGstDefaults?> Handle(int id, CancellationToken cancellationToken) =>
        _store.FindGstDefaultsAsync(id, cancellationToken);
}
