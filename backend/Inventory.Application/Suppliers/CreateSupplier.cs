using Inventory.Domain.Suppliers;

namespace Inventory.Application.Suppliers;

public sealed class CreateSupplier
{
    private readonly ISupplierStore _store;

    public CreateSupplier(ISupplierStore store)
    {
        _store = store;
    }

    /// <summary>
    /// Records a supplier. The optional website URL is normalized by
    /// <see cref="SupplierUrlPolicy"/> before anything is stored (issue #847), so a refused value
    /// creates nothing at all.
    /// </summary>
    public async Task<SupplierWriteResult> Handle(SupplierFields fields, CancellationToken cancellationToken)
    {
        if (!SupplierUrlPolicy.TryNormalize(fields.Url, out var normalizedUrl, out var urlError))
            return SupplierWriteResult.Invalid(urlError!);

        // The free-text limits (issue #1286), checked before anything is stored.
        if (SupplierFieldLengthPolicy.ValidateCreate(
                fields.Name, fields.ContactName, fields.Phone, fields.Email, fields.Address) is { } lengthError)
            return SupplierWriteResult.Invalid(lengthError);

        return SupplierWriteResult.Success(
            await _store.AddAsync(fields with { Url = normalizedUrl }, cancellationToken));
    }
}
