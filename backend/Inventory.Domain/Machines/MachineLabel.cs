using System.Globalization;

namespace Inventory.Domain.Machines;

/// <summary>
/// The one way a machine is named in a caller-facing message (issue #1240), beside
/// <see cref="Inventory.Domain.Products.ProductLabel"/> and for the same reason.
///
/// A refused pick reaches the operator as the message text alone, and a pick run across several
/// machines is a matrix of rows: naming the product the refusal is about is only half of "which row
/// do I fix?", so the machine the row was picked for is named the same way. The ID stays in the
/// message, because it is what identifies the machine unambiguously when two of them share a name.
///
/// A machine whose name is not known - a caller that never loaded one, or a blank stored name -
/// falls back to the bare "machine 7" wording rather than printing an empty pair of brackets.
/// </summary>
public static class MachineLabel
{
    /// <summary>The machine inside a sentence: <c>machine Front Lobby (7)</c>.</summary>
    public static string Phrase(long machineId, string? machineName) =>
        $"machine {Identify(machineId, machineName)}";

    private static string Identify(long machineId, string? machineName)
    {
        var id = machineId.ToString(CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(machineName) ? id : $"{machineName} ({id})";
    }
}
