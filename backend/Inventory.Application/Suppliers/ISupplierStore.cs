namespace Inventory.Application.Suppliers;

/// <summary>
/// Narrow persistence port for suppliers, owned by the Application layer.
/// </summary>
public interface ISupplierStore
{
    Task<IReadOnlyList<SupplierRecord>> ListOrderedByNameAsync(CancellationToken cancellationToken);

    Task<SupplierRecord?> FindByIdAsync(int id, CancellationToken cancellationToken);

    Task<SupplierRecord> AddAsync(string name, string? contactName, string? phone, string? email, string? address, CancellationToken cancellationToken);

    Task<SupplierRecord?> UpdateAsync(int id, string name, string? contactName, string? phone, string? email, string? address, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(int id, CancellationToken cancellationToken);
}
