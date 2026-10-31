using Inventory.Application.Categories;
using Inventory.Domain.Categories;
using Inventory.Domain.Providers;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="ICategoryStore"/>. It lives in Inventory.Infrastructure
/// beside the <see cref="AppDbContext"/> and the
/// <see cref="Inventory.Infrastructure.Models.Category"/> entity it reads, which moved there in
/// issue #307; this adapter followed them in issue #309 (Persistence 8/8 of #153).
///
/// <para><strong>The write side arrived with issue #736</strong>, and with it the refusals that
/// depend on stored rows: a name another category of this business already uses, a rename of a
/// category the business's sales provider manages, and - since issue #1026 - a delete of one.
/// Each is read, compared and acted on as one operation - inside a relational transaction - because
/// the uniqueness rule is not a database constraint (the schema is deliberately unchanged here), so
/// the window between looking and writing is the only thing holding it.</para>
///
/// Tenant scoping stays the central <see cref="AppDbContext"/> query filters and
/// <c>BusinessOwnershipEnforcer</c>: the uniqueness check therefore compares this business's
/// categories only, which is exactly the per-business rule
/// <see cref="CategoryCataloguePolicy"/> states.
/// </summary>
public sealed class EfCategoryStore : ICategoryStore
{
    private readonly AppDbContext _db;

    public EfCategoryStore(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<CategoryRecord>> ListOrderedByNameAsync(CancellationToken cancellationToken) =>
        await _db.Categories
            .AsNoTracking()
            .OrderBy(c => c.Name)
            .Select(c => new CategoryRecord(c.Id, c.Name, c.Description, c.ExternalCategoryId != null))
            .ToListAsync(cancellationToken);

    public async Task<CategoryRecord?> FindByIdAsync(long id, CancellationToken cancellationToken)
    {
        var entity = await _db.Categories.FindAsync([id], cancellationToken);
        return entity is null
            ? null
            : new CategoryRecord(entity.Id, entity.Name, entity.Description, entity.ExternalCategoryId is not null);
    }

    public async Task<CategoryWriteAttempt> CreateAsync(
        CategoryWriteFields fields, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fields);

        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(cancellationToken)
            : null;

        if (CategoryCataloguePolicy.Validate(fields.Name) is { } invalid)
            return CategoryWriteAttempt.Refused(invalid);

        var name = CategoryCataloguePolicy.Normalize(fields.Name!);
        if (await NameIsTakenAsync(name, excludedCategoryId: null, cancellationToken))
            return CategoryWriteAttempt.Refused(CategoryCataloguePolicy.DuplicateNameMessage(name));

        var category = new Category { Name = name, Description = fields.Description };
        _db.Categories.Add(category);
        await _db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);

        return CategoryWriteAttempt.Succeeded(category.Id);
    }

    /// <summary>
    /// Writes the category's name and description, refusing a rename the business's sales provider
    /// owns and a name another category already uses. Submitting the stored name is not a rename, so
    /// a provider-managed category's description stays editable through the same request shape.
    /// </summary>
    public async Task<CategoryWriteAttempt> UpdateAsync(
        long id, CategoryWriteFields fields, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fields);

        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(cancellationToken)
            : null;

        var category = await _db.Categories.FirstOrDefaultAsync(
            candidate => candidate.Id == id, cancellationToken);
        if (category is null) return CategoryWriteAttempt.NotFound();

        // The use case validated the submitted name already; applied again at the write itself, so
        // the column cannot be written blank by a future caller that forgets.
        if (CategoryCataloguePolicy.Validate(fields.Name) is { } invalid)
            return CategoryWriteAttempt.Refused(invalid);

        var name = CategoryCataloguePolicy.Normalize(fields.Name!);
        var renaming = !string.Equals(category.Name, name, StringComparison.Ordinal);

        if (renaming && category.ExternalCategoryId is not null)
        {
            return CategoryWriteAttempt.Refused(ProviderManagedCatalogueFields.CategoryNameRefusal(
                await EfSalesProviderQueries.CurrentBusinessSalesProviderAsync(_db, cancellationToken)));
        }

        if (renaming && await NameIsTakenAsync(name, excludedCategoryId: id, cancellationToken))
            return CategoryWriteAttempt.Refused(CategoryCataloguePolicy.DuplicateNameMessage(name));

        category.Name = name;
        category.Description = fields.Description;
        await _db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);

        return CategoryWriteAttempt.Succeeded(category.Id);
    }

    /// <summary>
    /// Deletes the category only while no product uses it, and never a category the business's
    /// sales provider names (issue #1026) - checked first, so that refusal wins when both apply. The
    /// checks and the delete share one transaction, because <c>Product.CategoryId</c> is set to null
    /// when its category disappears: a product categorised between a separate count and this delete
    /// would be left uncategorised rather than the delete being refused.
    /// </summary>
    public async Task<CategoryWriteAttempt> DeleteAsync(long id, CancellationToken cancellationToken)
    {
        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(cancellationToken)
            : null;

        var category = await _db.Categories.FirstOrDefaultAsync(
            candidate => candidate.Id == id, cancellationToken);
        if (category is null) return CategoryWriteAttempt.NotFound();

        if (category.ExternalCategoryId is not null)
        {
            return CategoryWriteAttempt.Refused(CategoryCataloguePolicy.ProviderCategoryDeletionRefusal(
                await EfSalesProviderQueries.CurrentBusinessSalesProviderAsync(_db, cancellationToken)));
        }

        var products = await _db.Products.CountAsync(product => product.CategoryId == id, cancellationToken);
        if (CategoryCataloguePolicy.DeletionRefusal(products) is { } refusal)
            return CategoryWriteAttempt.Refused(refusal);

        _db.Categories.Remove(category);
        await _db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);

        return CategoryWriteAttempt.Succeeded(id);
    }

    /// <summary>
    /// Whether this business already has a category called <paramref name="name"/>, ignoring case
    /// and surrounding space.
    ///
    /// <para>The names are compared in memory, over this business's own categories only, rather than
    /// in SQL: SQLite's <c>=</c> on text is case-sensitive unless a column declares otherwise, and
    /// its <c>LIKE</c> is case-insensitive only for ASCII, so neither answers the question the policy
    /// asks. A business has tens of categories, and this comparison is the same
    /// <see cref="StringComparer.OrdinalIgnoreCase"/> one
    /// <see cref="EfLocalCatalogSnapshotProvider"/> already applies to catalogue names.</para>
    /// </summary>
    private async Task<bool> NameIsTakenAsync(
        string name, long? excludedCategoryId, CancellationToken cancellationToken)
    {
        var names = await _db.Categories
            .AsNoTracking()
            .Where(category => excludedCategoryId == null || category.Id != excludedCategoryId)
            .Select(category => category.Name)
            .ToListAsync(cancellationToken);

        return names.Any(existing =>
            string.Equals(existing.Trim(), name, StringComparison.OrdinalIgnoreCase));
    }
}
