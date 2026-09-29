namespace Inventory.Application.Expenses;

/// <summary>
/// A candidate supporting document awaiting validation and storage, decoupled from ASP.NET Core's
/// <c>IFormFile</c> so Application never depends on it.
/// </summary>
public sealed record ExpenseAttachmentInput(string FileName, long Length, Func<Stream> OpenReadStream);
