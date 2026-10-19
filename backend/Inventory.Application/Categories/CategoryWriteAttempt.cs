namespace Inventory.Application.Categories;

/// <summary>What a category write did (issue #736).</summary>
public enum CategoryWriteOutcome
{
    /// <summary>The category was created, updated or deleted.</summary>
    Succeeded,

    /// <summary>No category with that id is visible to the caller's business.</summary>
    NotFound,

    /// <summary>
    /// The write was refused with a message a caller may be shown: the name is missing or already
    /// used in this business, the name is managed by the business's sales provider, or products
    /// still use the category being deleted.
    /// </summary>
    Refused,
}

/// <summary>
/// The outcome of one category write (issue #736).
///
/// <para>The refusals that depend on stored data - a duplicate name, a provider-managed name, a
/// category products still use - are decided inside the store, in the same operation as the write,
/// so the uniqueness check cannot be invalidated between the look and the insert. Validation of the
/// submitted name itself needs no database and stays in the use case.</para>
/// </summary>
public sealed record CategoryWriteAttempt(CategoryWriteOutcome Outcome, long? CategoryId, string? Refusal)
{
    public static CategoryWriteAttempt Succeeded(long? categoryId = null) =>
        new(CategoryWriteOutcome.Succeeded, categoryId, null);

    public static CategoryWriteAttempt NotFound() => new(CategoryWriteOutcome.NotFound, null, null);

    public static CategoryWriteAttempt Refused(string refusal) =>
        new(CategoryWriteOutcome.Refused, null, refusal);
}
