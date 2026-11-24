using Inventory.Domain.FieldLengths;

namespace Inventory.Domain.Purchases;

/// <summary>
/// The longest title and notes a purchase may be given (issue #1286, audit #1226 finding DB-03),
/// measured after trimming. The invoice number keeps <see cref="PurchaseInvoiceNumberPolicy"/>'s own
/// limit, and a title that falls back to the uploaded document's file name is not judged here (the
/// upload file-name fields are outside this rule). A create is judged on what it submits; an update
/// is judged against the stored row, so a purchase that already holds a longer value can still be
/// saved with a different field changed (<see cref="FieldLengthLimit.ChangeRefusal"/>). Stored
/// values are never shortened.
/// </summary>
public static class PurchaseFieldLengthPolicy
{
    /// <summary>The longest purchase title.</summary>
    public const int MaxTitleLength = 200;

    /// <summary>The longest purchase notes.</summary>
    public const int MaxNotesLength = 2_000;

    /// <summary>The field names the refusals use.</summary>
    public const string TitleLabel = "Title";

    /// <summary>The field names the refusals use.</summary>
    public const string NotesLabel = "Notes";

    /// <summary>The first refusal for a new purchase's text, or <c>null</c> when both fields fit.</summary>
    public static string? ValidateCreate(string? title, string? notes) =>
        FieldLengthLimit.Refusal(TitleLabel, title, MaxTitleLength)
        ?? FieldLengthLimit.Refusal(NotesLabel, notes, MaxNotesLength);

    /// <summary>
    /// The first refusal for an update's text judged against the stored purchase. A blank title on
    /// an update keeps the stored one, so it is never a change.
    /// </summary>
    public static string? ValidateUpdate(string? storedTitle, string? storedNotes, string? title, string? notes) =>
        FieldLengthLimit.ChangeRefusal(TitleLabel, storedTitle, title, MaxTitleLength)
        ?? FieldLengthLimit.ChangeRefusal(NotesLabel, storedNotes, notes, MaxNotesLength);
}
