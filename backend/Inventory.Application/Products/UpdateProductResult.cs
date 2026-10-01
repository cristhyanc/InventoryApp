namespace Inventory.Application.Products;

public sealed record UpdateProductResult(UpdateProductOutcome Outcome, string? ValidationError)
{
    public static UpdateProductResult Success() => new(UpdateProductOutcome.Success, null);

    public static UpdateProductResult NotFound() => new(UpdateProductOutcome.NotFound, null);

    public static UpdateProductResult Invalid(string error) => new(UpdateProductOutcome.Invalid, error);
}
