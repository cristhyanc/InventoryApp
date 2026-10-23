namespace Inventory.Application.Suppliers;

public sealed class UpdateSupplier
{
    private readonly ISupplierStore _store;

    public UpdateSupplier(ISupplierStore store)
    {
        _store = store;
    }

    /// <summary>
    /// Replaces a supplier's details. The optional website URL is normalized by
    /// <see cref="SupplierUrlPolicy"/> before the supplier is even looked up (issue #847), so a
    /// refused value leaves the stored supplier exactly as it was.
    /// </summary>
    public async Task<SupplierWriteResult> Handle(
        int id, SupplierFields fields, CancellationToken cancellationToken)
    {
        if (!SupplierUrlPolicy.TryNormalize(fields.Url, out var normalizedUrl, out var urlError))
            return SupplierWriteResult.Invalid(urlError!);

        var updated = await _store.UpdateAsync(id, fields with { Url = normalizedUrl }, cancellationToken);

        return updated is null ? SupplierWriteResult.NotFound() : SupplierWriteResult.Success(updated);
    }
}
