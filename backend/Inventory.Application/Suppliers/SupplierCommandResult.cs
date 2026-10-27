namespace Inventory.Application.Suppliers;

/// <summary>
/// How a supplier command that returns no supplier ended: the delete, and the "Not used" switch of
/// issue #934. It reuses <see cref="SupplierWriteOutcome"/> rather than declaring a second
/// vocabulary, so every supplier write path maps to HTTP the same way - success, "no such supplier
/// of this business", or a refusal carrying the sentence the operator reads.
/// </summary>
public sealed record SupplierCommandResult(SupplierWriteOutcome Outcome, string? ValidationError)
{
    /// <summary>The command was applied.</summary>
    public static SupplierCommandResult Success { get; } = new(SupplierWriteOutcome.Success, null);

    /// <summary>No supplier with that id is visible to the caller's business.</summary>
    public static SupplierCommandResult NotFound { get; } = new(SupplierWriteOutcome.NotFound, null);

    /// <summary>The command was refused and nothing was written.</summary>
    public static SupplierCommandResult Invalid(string error) =>
        new(SupplierWriteOutcome.Invalid, error);
}
