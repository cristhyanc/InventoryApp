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
    public async Task<SupplierWriteResult> Handle(
        string name,
        string? contactName,
        string? phone,
        string? email,
        string? address,
        string? url,
        CancellationToken cancellationToken)
    {
        if (!SupplierUrlPolicy.TryNormalize(url, out var normalizedUrl, out var urlError))
            return SupplierWriteResult.Invalid(urlError!);

        return SupplierWriteResult.Success(
            await _store.AddAsync(name, contactName, phone, email, address, normalizedUrl, cancellationToken));
    }
}
