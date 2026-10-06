using Inventory.Domain.Gst;

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

    /// <summary>
    /// The supplier's explicitly configured GST defaults (issue #430), or <c>null</c> when no
    /// supplier with <paramref name="id"/> is visible to the caller's business. A supplier nobody
    /// has configured answers <see cref="SupplierGstDefaults.None"/>.
    /// </summary>
    Task<SupplierGstDefaults?> FindGstDefaultsAsync(int id, CancellationToken cancellationToken);

    /// <summary>
    /// Stores the supplier's GST defaults and nothing else: no purchase, purchase line, charge or
    /// stock movement is touched (AGENTS.md § Purchase GST classification). Returns <c>false</c>
    /// when no supplier with <paramref name="id"/> was updated.
    /// </summary>
    Task<bool> SetGstDefaultsAsync(int id, SupplierGstDefaults defaults, CancellationToken cancellationToken);
}
