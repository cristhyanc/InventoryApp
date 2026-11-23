using Inventory.Domain.FieldLengths;

namespace Inventory.Domain.Machines;

/// <summary>
/// The longest name a machine a person creates or renames may be given (issue #1286, audit #1226
/// finding DB-03), measured after trimming. A rename is judged against the stored name, so
/// submitting the stored name again is never refused (<see cref="FieldLengthLimit.ChangeRefusal"/>).
/// Stored names - including the provider's, which are outside this rule - are never shortened. The
/// blank-name rule stays <see cref="MachineCataloguePolicy.Validate"/>'s.
/// </summary>
public static class MachineFieldLengthPolicy
{
    /// <summary>The longest machine name.</summary>
    public const int MaxNameLength = 200;

    /// <summary>The field name the refusal uses.</summary>
    public const string NameLabel = "Machine name";

    /// <summary>The refusal for a new machine's name, or <c>null</c> when it fits.</summary>
    public static string? ValidateCreate(string? name) =>
        FieldLengthLimit.Refusal(NameLabel, name, MaxNameLength);

    /// <summary>The refusal for a rename judged against the stored name.</summary>
    public static string? ValidateRename(string? storedName, string? name) =>
        FieldLengthLimit.ChangeRefusal(NameLabel, storedName, name, MaxNameLength);
}
