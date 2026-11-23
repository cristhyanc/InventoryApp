using Inventory.Domain.FieldLengths;

namespace Inventory.Domain.Expenses;

/// <summary>
/// The longest description and notes an operating expense may be given (issue #1286, audit #1226
/// finding DB-03), measured after trimming. A create is judged on what it submits; an update is
/// judged against the stored row, so an expense that already holds a longer value can still be
/// saved with a different field changed (<see cref="FieldLengthLimit.ChangeRefusal"/>). Stored
/// values are never shortened. The required-description rule stays
/// <see cref="OperatingExpenseDetails"/>'s.
/// </summary>
public static class OperatingExpenseFieldLengthPolicy
{
    /// <summary>The longest expense description.</summary>
    public const int MaxDescriptionLength = 500;

    /// <summary>The longest expense notes.</summary>
    public const int MaxNotesLength = 2_000;

    /// <summary>The field names the refusals use.</summary>
    public const string DescriptionLabel = "Description";

    /// <summary>The field names the refusals use.</summary>
    public const string NotesLabel = "Notes";

    /// <summary>The first refusal for a new expense's text, or <c>null</c> when both fields fit.</summary>
    public static string? ValidateCreate(string? description, string? notes) =>
        FieldLengthLimit.Refusal(DescriptionLabel, description, MaxDescriptionLength)
        ?? FieldLengthLimit.Refusal(NotesLabel, notes, MaxNotesLength);

    /// <summary>The first refusal for an update's text judged against the stored expense.</summary>
    public static string? ValidateUpdate(
        string? storedDescription, string? storedNotes, string? description, string? notes) =>
        FieldLengthLimit.ChangeRefusal(DescriptionLabel, storedDescription, description, MaxDescriptionLength)
        ?? FieldLengthLimit.ChangeRefusal(NotesLabel, storedNotes, notes, MaxNotesLength);
}
