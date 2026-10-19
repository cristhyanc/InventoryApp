using Inventory.Domain.Categories;

namespace Inventory.Application.Categories;

/// <summary>
/// The category update use case (issue #736). The submitted name is validated here; whether it
/// clashes with another category of this business, and whether the business's sales provider manages
/// this category's name, are statements about stored data and are decided by the store inside the
/// same operation as the write.
///
/// <para>Submitting the name the category already has is not a rename, so a client may send the
/// whole category back - including for a provider-managed one, whose description stays editable.</para>
/// </summary>
public sealed class UpdateCategory
{
    private readonly ICategoryStore _store;

    public UpdateCategory(ICategoryStore store)
    {
        _store = store;
    }

    public Task<CategoryWriteAttempt> Handle(
        long id, CategoryWriteFields fields, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fields);

        var error = CategoryCataloguePolicy.Validate(fields.Name);
        return error is not null
            ? Task.FromResult(CategoryWriteAttempt.Refused(error))
            : _store.UpdateAsync(id, fields, cancellationToken);
    }
}
