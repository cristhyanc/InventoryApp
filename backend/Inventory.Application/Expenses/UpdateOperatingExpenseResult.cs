namespace Inventory.Application.Expenses;

public sealed class UpdateOperatingExpenseResult
{
    private UpdateOperatingExpenseResult(OperatingExpenseRecord? record, string? validationError, bool isNotFound)
    {
        Record = record;
        ValidationError = validationError;
        IsNotFound = isNotFound;
    }

    public OperatingExpenseRecord? Record { get; }

    public string? ValidationError { get; }

    public bool IsNotFound { get; }

    public bool IsValid => ValidationError is null && !IsNotFound;

    public static UpdateOperatingExpenseResult Success(OperatingExpenseRecord record) => new(record, null, false);

    public static UpdateOperatingExpenseResult Invalid(string validationError) => new(null, validationError, false);

    public static UpdateOperatingExpenseResult NotFound() => new(null, null, true);
}
