using Inventory.Application.Suppliers;
using Inventory.Domain.Gst;
using Inventory.Domain.Suppliers;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="ISupplierStore"/>. It lives in Inventory.Infrastructure
/// beside the <see cref="AppDbContext"/> and the <see cref="Supplier"/> entity it reads, which moved
/// there in issue #307; this adapter followed them in issue #309 (Persistence 8/8 of #153).
///
/// <para>Since issue #934 the write methods apply <see cref="SupplierPresetLinkPolicy"/> to the row
/// they loaded, inside the same unit of work as the write. The policy itself is the Domain's and is
/// not restated here: this adapter supplies the two facts it reads - whether the row carries a
/// preset id, and what it currently holds - and returns the sentence it answers with.</para>
/// </summary>
public sealed class EfSupplierStore : ISupplierStore
{
    private readonly AppDbContext _db;

    public EfSupplierStore(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<SupplierRecord>> ListOrderedByNameAsync(CancellationToken cancellationToken) =>
        await _db.Suppliers
            .AsNoTracking()
            .OrderBy(s => s.Name)
            .Select(s => ToRecord(s))
            .ToListAsync(cancellationToken);

    /// <summary>
    /// The same list without the suppliers marked "Not used" (issue #934). It is a second statement
    /// rather than a filter the caller passes in, so a picker cannot ask for the hidden ones by
    /// getting a flag wrong.
    /// </summary>
    public async Task<IReadOnlyList<SupplierRecord>> ListSelectableOrderedByNameAsync(
        CancellationToken cancellationToken) =>
        await _db.Suppliers
            .AsNoTracking()
            .Where(s => !s.IsHidden)
            .OrderBy(s => s.Name)
            .Select(s => ToRecord(s))
            .ToListAsync(cancellationToken);

    public async Task<SupplierRecord?> FindByIdAsync(int id, CancellationToken cancellationToken)
    {
        var entity = await _db.Suppliers.FindAsync([id], cancellationToken);
        return entity is null ? null : ToRecord(entity);
    }

    public async Task<SupplierRecord> AddAsync(SupplierFields fields, CancellationToken cancellationToken)
    {
        var entity = new Supplier
        {
            Name = fields.Name,
            ContactName = fields.ContactName,
            Phone = fields.Phone,
            Email = fields.Email,
            Address = fields.Address,
            Url = fields.Url,

            // No SupplierPresetId and not hidden: a supplier a business creates itself is its own
            // row, and only the approval's provisioning copy carries a link (issue #934).
        };
        _db.Suppliers.Add(entity);
        await _db.SaveChangesAsync(cancellationToken);
        return ToRecord(entity);
    }

    /// <summary>
    /// Replaces the supplier's details, or refuses a linked copy's rename or website change.
    ///
    /// <para>The refusal is decided on the tracked row this method loaded and before anything is
    /// assigned, so a refused update writes nothing at all - not even the contact fields that would
    /// have been allowed on their own.</para>
    /// </summary>
    public async Task<SupplierWriteResult> UpdateAsync(
        int id, SupplierFields fields, CancellationToken cancellationToken)
    {
        var entity = await _db.Suppliers.FindAsync([id], cancellationToken);
        if (entity is null) return SupplierWriteResult.NotFound();

        var refusal = SupplierPresetLinkPolicy.RefuseUpdate(
            SupplierPresetLinkPolicy.IsLinked(entity.SupplierPresetId),
            entity.Name,
            entity.Url,
            fields.Name,
            fields.Url);

        if (refusal is not null) return SupplierWriteResult.Invalid(refusal);

        entity.Name = fields.Name;
        entity.ContactName = fields.ContactName;
        entity.Phone = fields.Phone;
        entity.Email = fields.Email;
        entity.Address = fields.Address;
        entity.Url = fields.Url;
        await _db.SaveChangesAsync(cancellationToken);
        return SupplierWriteResult.Success(ToRecord(entity));
    }

    public async Task<SupplierCommandResult> DeleteAsync(int id, CancellationToken cancellationToken)
    {
        var entity = await _db.Suppliers.FindAsync([id], cancellationToken);
        if (entity is null) return SupplierCommandResult.NotFound;

        var refusal = SupplierPresetLinkPolicy.RefuseDelete(
            SupplierPresetLinkPolicy.IsLinked(entity.SupplierPresetId));

        if (refusal is not null) return SupplierCommandResult.Invalid(refusal);

        _db.Suppliers.Remove(entity);
        await _db.SaveChangesAsync(cancellationToken);
        return SupplierCommandResult.Success;
    }

    /// <summary>Writes the "Not used" column only; no other supplier field is touched.</summary>
    public async Task<SupplierCommandResult> SetHiddenAsync(
        int id, bool isHidden, CancellationToken cancellationToken)
    {
        var entity = await _db.Suppliers.FindAsync([id], cancellationToken);
        if (entity is null) return SupplierCommandResult.NotFound;

        var refusal = SupplierPresetLinkPolicy.RefuseHide(
            SupplierPresetLinkPolicy.IsLinked(entity.SupplierPresetId));

        if (refusal is not null) return SupplierCommandResult.Invalid(refusal);

        entity.IsHidden = isHidden;
        await _db.SaveChangesAsync(cancellationToken);
        return SupplierCommandResult.Success;
    }

    public async Task<SupplierGstDefaults?> FindGstDefaultsAsync(int id, CancellationToken cancellationToken)
    {
        var row = await _db.Suppliers
            .AsNoTracking()
            .Where(supplier => supplier.Id == id)
            .Select(supplier => new
            {
                supplier.ProductLineGstDefault,
                supplier.DeliveryGstDefault,
                supplier.PackageGstDefault,
            })
            .SingleOrDefaultAsync(cancellationToken);

        return row is null
            ? null
            : new SupplierGstDefaults(row.ProductLineGstDefault, row.DeliveryGstDefault, row.PackageGstDefault);
    }

    /// <summary>
    /// Writes the three default columns only; no other supplier field is touched. A linked copy is
    /// not refused here (issue #934): the GST defaults are the business's own accounting settings,
    /// and the link locks the supplier's identity rather than what the business records about its
    /// invoices.
    /// </summary>
    public async Task<bool> SetGstDefaultsAsync(
        int id, SupplierGstDefaults defaults, CancellationToken cancellationToken)
    {
        var entity = await _db.Suppliers.FindAsync([id], cancellationToken);
        if (entity is null) return false;

        entity.ProductLineGstDefault = defaults.ProductLines;
        entity.DeliveryGstDefault = defaults.Delivery;
        entity.PackageGstDefault = defaults.Package;
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static SupplierRecord ToRecord(Supplier entity) =>
        new(
            entity.Id,
            entity.Name,
            entity.ContactName,
            entity.Phone,
            entity.Email,
            entity.Address,
            entity.Url,
            entity.SupplierPresetId,
            entity.IsHidden);
}
