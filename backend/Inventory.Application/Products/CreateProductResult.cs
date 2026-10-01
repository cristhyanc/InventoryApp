namespace Inventory.Application.Products;

public sealed record CreateProductResult(bool IsValid, string? ValidationError, long? ProductId)
{
    public static CreateProductResult Success(long productId) => new(true, null, productId);

    public static CreateProductResult Invalid(string error) => new(false, error, null);
}
