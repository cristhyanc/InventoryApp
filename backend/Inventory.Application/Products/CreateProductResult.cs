namespace Inventory.Application.Products;

/// <summary>
/// What a product create did.
///
/// <para><strong>Since issue #1051 it has a third answer</strong>: the master product the operator
/// picked does not stand, which is a <em>conflict</em> rather than a validation failure - the
/// submitted values are valid and what changed is the state they were chosen against. It carries
/// <see cref="ConflictCode"/> so a client can tell the three apart and offer "choose again" or
/// "create without the link"; nothing was created.</para>
/// </summary>
/// <param name="IsValid">Whether the product was created.</param>
/// <param name="ValidationError">The caller-safe sentence of a refusal or a conflict.</param>
/// <param name="ProductId">The created product's key.</param>
/// <param name="ConflictCode">
/// The stable code of a master-product conflict and of nothing else: one of
/// <c>Inventory.Domain.PriceCatalogue.MasterProductSelectionPolicy</c>'s three.
/// </param>
public sealed record CreateProductResult(
    bool IsValid,
    string? ValidationError,
    long? ProductId,
    string? ConflictCode = null)
{
    public static CreateProductResult Success(long productId) => new(true, null, productId);

    public static CreateProductResult Invalid(string error) => new(false, error, null);

    /// <summary>
    /// The selected master product does not stand (issue #1051). Nothing was created: the operator
    /// asked for a product linked to that master product, and this is not one.
    /// </summary>
    public static CreateProductResult Conflict(string code, string message) =>
        new(false, message, null, code);
}
