using System.Globalization;

namespace Inventory.Domain.Machines;

/// <summary>
/// The deterministic invariants on a machine a business maintains by hand (issue #740, parent #329
/// child G1): it has a name, and a machine the business's sales provider named still carries that
/// provider's own id, which this application never edits.
///
/// <para><strong>A machine is name-only, deliberately.</strong> A business with no sales provider
/// has to be able to record the machines it operates before anything external knows about them, so
/// the one rule a submitted machine has to satisfy is that it is called something. There is no
/// per-business name uniqueness: two machines in one corridor may legitimately be called "Level 3",
/// and nothing in the application resolves a machine by its name - the local id does that.</para>
///
/// <para><strong>The default name is the one the backfill writes.</strong>
/// <see cref="DefaultName"/> is "Machine" followed by the provider's own id, for a machine that was
/// only ever referenced by stored rows that carried no name. The <c>AddMachines</c> migration writes
/// exactly this shape in SQL (<c>'Machine ' || "MachineId"</c>) because a migration cannot call into
/// the Domain; this is where that shape is stated, and the migration test compares the two.</para>
///
/// <para>Deterministic: every answer is a function of its arguments, so the use case, the adapter
/// and a test decide identically.</para>
/// </summary>
public static class MachineCataloguePolicy
{
    public const string NameRequiredMessage = "Machine name is required.";

    /// <summary>Returns the validation error message, or <c>null</c> when the name is usable.</summary>
    public static string? Validate(string? name) =>
        string.IsNullOrWhiteSpace(name) ? NameRequiredMessage : null;

    /// <summary>
    /// The name as it is stored: trimmed, because trailing space is not a different machine.
    /// </summary>
    public static string Normalize(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.Trim();
    }

    /// <summary>
    /// The name a machine gets when every stored row that referenced it carried none: the word
    /// "Machine" and the provider's own identifier for it, so an operator can tell two backfilled
    /// machines apart and rename them afterwards.
    /// </summary>
    public static string DefaultName(string externalMachineId)
    {
        ArgumentNullException.ThrowIfNull(externalMachineId);
        return string.Create(CultureInfo.InvariantCulture, $"Machine {externalMachineId}");
    }
}
