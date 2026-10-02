namespace Inventory.Application.Purchases;

/// <summary>
/// The uploaded supporting document for a new purchase, decoupled from ASP.NET Core's
/// <c>IFormFile</c> so Application never depends on it (the same shape
/// <see cref="Inventory.Application.Expenses.ExpenseAttachmentInput"/> uses).
/// </summary>
public sealed record PurchaseFileInput(string FileName, string? ContentType, long Length, Func<Stream> OpenReadStream);
