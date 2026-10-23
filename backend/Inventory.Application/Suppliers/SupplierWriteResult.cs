namespace Inventory.Application.Suppliers;

/// <summary>How a supplier create or update ended (issue #847).</summary>
public enum SupplierWriteOutcome
{
    /// <summary>The supplier was created or updated.</summary>
    Success,

    /// <summary>No supplier with that id is visible to the caller's business.</summary>
    NotFound,

    /// <summary>A submitted field was refused; nothing was written.</summary>
    Invalid,
}

/// <summary>
/// The answer a supplier create or update gives the HTTP boundary. It carries the validation
/// message rather than throwing, the same shape <c>GstRuleUpdateResult</c> uses for the GST
/// defaults resource, so the controller maps an outcome to a status code and nothing else.
/// </summary>
public sealed record SupplierWriteResult(
    SupplierWriteOutcome Outcome, SupplierRecord? Supplier, string? ValidationError)
{
    public static SupplierWriteResult Success(SupplierRecord supplier) =>
        new(SupplierWriteOutcome.Success, supplier, null);

    public static SupplierWriteResult NotFound() => new(SupplierWriteOutcome.NotFound, null, null);

    public static SupplierWriteResult Invalid(string error) =>
        new(SupplierWriteOutcome.Invalid, null, error);
}
