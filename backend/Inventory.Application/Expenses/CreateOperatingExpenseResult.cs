namespace Inventory.Application.Expenses;

public sealed class CreateOperatingExpenseResult
{
    private CreateOperatingExpenseResult(OperatingExpenseRecord? record, string? validationError)
    {
        Record = record;
        ValidationError = validationError;
    }

    public OperatingExpenseRecord? Record { get; }

    public string? ValidationError { get; }

    public bool IsValid => ValidationError is null;

    public static CreateOperatingExpenseResult Success(OperatingExpenseRecord record) => new(record, null);

    public static CreateOperatingExpenseResult Invalid(string validationError) => new(null, validationError);
}
