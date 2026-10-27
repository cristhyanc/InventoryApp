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
    ///
    /// <para>The normalized value is also what the store compares against a linked copy's stored
    /// website (issue #934): a blank submission means "no website" there too, so clearing a linked
    /// copy's website is refused while re-submitting the one it already has is not.</para>
    /// </summary>
    public async Task<SupplierWriteResult> Handle(
        int id, SupplierFields fields, CancellationToken cancellationToken)
    {
        if (!SupplierUrlPolicy.TryNormalize(fields.Url, out var normalizedUrl, out var urlError))
            return SupplierWriteResult.Invalid(urlError!);

        return await _store.UpdateAsync(id, fields with { Url = normalizedUrl }, cancellationToken);
    }
}
