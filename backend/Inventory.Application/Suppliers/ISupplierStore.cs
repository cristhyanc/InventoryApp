using Inventory.Domain.Gst;

namespace Inventory.Application.Suppliers;

/// <summary>
/// Narrow persistence port for suppliers, owned by the Application layer.
/// </summary>
public interface ISupplierStore
{
    /// <summary>
    /// Every supplier of the caller's business, ordered by name, including the ones marked "Not
    /// used" (issue #934). The Suppliers page is the surface that has to show a hidden supplier in
    /// order to switch it back on; <see cref="ListSelectableOrderedByNameAsync"/> is what a picker
    /// asks for.
    /// </summary>
    Task<IReadOnlyList<SupplierRecord>> ListOrderedByNameAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The suppliers a purchase, a supplier order or a product form may be recorded against:
    /// everything <see cref="ListOrderedByNameAsync"/> returns except the ones marked "Not used"
    /// (issue #934). A hidden supplier that is already on a stored purchase or order still resolves
    /// through its own relationship, which is what keeps history readable.
    /// </summary>
    Task<IReadOnlyList<SupplierRecord>> ListSelectableOrderedByNameAsync(CancellationToken cancellationToken);

    Task<SupplierRecord?> FindByIdAsync(int id, CancellationToken cancellationToken);

    /// <summary><paramref name="fields"/>.Url has already been normalized by <see cref="SupplierUrlPolicy"/>.</summary>
    Task<SupplierRecord> AddAsync(SupplierFields fields, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the supplier's details. <paramref name="fields"/>.Url has already been normalized
    /// by <see cref="SupplierUrlPolicy"/>.
    ///
    /// <para>The implementation applies
    /// <c>Inventory.Domain.Suppliers.SupplierPresetLinkPolicy.RefuseUpdate</c> to the row it read,
    /// inside the same unit of work as the write (issue #934), and answers
    /// <see cref="SupplierWriteOutcome.Invalid"/> with that policy's message when the submitted name
    /// or website differs from a linked copy's. The read, the comparison and the write are one
    /// operation on purpose: a check made before the row was loaded would be a decision about a
    /// supplier that may have changed since.</para>
    /// </summary>
    Task<SupplierWriteResult> UpdateAsync(int id, SupplierFields fields, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the supplier, or refuses with
    /// <c>Inventory.Domain.Suppliers.SupplierPresetLinkPolicy.DeleteRefusedMessage</c> when the row
    /// it read is a copy of a platform supplier preset (issue #934).
    /// </summary>
    Task<SupplierCommandResult> DeleteAsync(int id, CancellationToken cancellationToken);

    /// <summary>
    /// Sets or clears the supplier's "Not used" switch (issue #934), writing that one column and
    /// nothing else. Refused with
    /// <c>Inventory.Domain.Suppliers.SupplierPresetLinkPolicy.HideRefusedMessage</c> for a supplier
    /// the business created itself.
    /// </summary>
    Task<SupplierCommandResult> SetHiddenAsync(int id, bool isHidden, CancellationToken cancellationToken);

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
    ///
    /// <para>A linked copy's GST defaults are the business's own accounting settings and are
    /// deliberately editable (issue #934): the link locks the supplier's identity, not what the
    /// business decides about its invoices.</para>
    /// </summary>
    Task<bool> SetGstDefaultsAsync(int id, SupplierGstDefaults defaults, CancellationToken cancellationToken);
}
