namespace Inventory.Application.Expenses;

/// <summary>
/// The stored-document facts a successful attachment upload adds to an operating expense.
/// </summary>
public sealed record OperatingExpenseAttachmentMetadata(
    string FileName,
    string StoredFileName,
    string ContentType,
    long FileSizeBytes);
