using Inventory.Domain.Categories;

namespace Inventory.Application.Categories;

/// <summary>
/// The category creation use case (issue #736, parent #329 decision P2): a business without a sales
/// provider has to be able to organise its own catalogue.
///
/// <para>It validates the submitted name - the one rule that needs no database - and leaves the
/// uniqueness refusal to the store, which decides it in the same operation as the insert.</para>
/// </summary>
public sealed class CreateCategory
{
    private readonly ICategoryStore _store;

    public CreateCategory(ICategoryStore store)
    {
        _store = store;
    }

    public Task<CategoryWriteAttempt> Handle(CategoryWriteFields fields, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fields);

        var error = CategoryCataloguePolicy.Validate(fields.Name);
        return error is not null
            ? Task.FromResult(CategoryWriteAttempt.Refused(error))
            : _store.CreateAsync(fields, cancellationToken);
    }
}
