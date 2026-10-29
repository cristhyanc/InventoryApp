namespace Inventory.Application.Expenses;

public sealed class UpdateOperatingExpenseResult
{
    private UpdateOperatingExpenseResult(
        OperatingExpenseRecord? record, string? validationError, bool isNotFound, string? conflictError)
    {
        Record = record;
        ValidationError = validationError;
        IsNotFound = isNotFound;
        ConflictError = conflictError;
    }

    public OperatingExpenseRecord? Record { get; }

    public string? ValidationError { get; }

    public bool IsNotFound { get; }

    /// <summary>
    /// Why the update was refused as a conflict rather than as invalid input (issue #968), or
    /// <c>null</c>. Today there is one: another replacement of this expense's attachment committed
    /// while this upload was in progress, so exactly one of them wins and this one is told to
    /// reload and try again. It is a different answer from a validation failure because nothing
    /// about the request was wrong.
    /// </summary>
    public string? ConflictError { get; }

    public bool IsConflict => ConflictError is not null;

    public bool IsValid => ValidationError is null && ConflictError is null && !IsNotFound;

    public static UpdateOperatingExpenseResult Success(OperatingExpenseRecord record) =>
        new(record, null, false, null);

    public static UpdateOperatingExpenseResult Invalid(string validationError) =>
        new(null, validationError, false, null);

    public static UpdateOperatingExpenseResult NotFound() => new(null, null, true, null);

    /// <summary>The update lost a race for the same attachment; nothing about the expense changed.</summary>
    public static UpdateOperatingExpenseResult Conflict(string conflictError) =>
        new(null, null, false, conflictError);
}
