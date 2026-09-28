using Inventory.Application.Documents;

namespace Inventory.Application.Expenses;

/// <summary>An opened supporting document ready to stream back to the caller.</summary>
public sealed record OperatingExpenseAttachmentResult(DocumentContent Document, string? FileName, string? ContentType);
