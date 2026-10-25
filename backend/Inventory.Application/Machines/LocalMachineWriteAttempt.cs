namespace Inventory.Application.Machines;

/// <summary>What a machine write did (issue #740).</summary>
public enum LocalMachineWriteOutcome
{
    /// <summary>The machine was created, renamed, deactivated or reactivated.</summary>
    Succeeded,

    /// <summary>No machine with that id is visible to the caller's business.</summary>
    NotFound,

    /// <summary>
    /// The write was refused with a message a caller may be shown: a missing name, or a rename or
    /// deactivation of a machine the business's sales provider names.
    /// </summary>
    Refused,
}

/// <summary>
/// The outcome of one machine write (issue #740), the same shape the category writes of issue #736
/// use.
///
/// <para>Submitted-name validation needs no database and stays in the use case; whether the machine
/// exists at all is a statement about stored rows and is decided by the store, in the same operation
/// as the write.</para>
/// </summary>
public sealed record LocalMachineWriteAttempt(LocalMachineWriteOutcome Outcome, long? MachineId, string? Refusal)
{
    public static LocalMachineWriteAttempt Succeeded(long? machineId = null) =>
        new(LocalMachineWriteOutcome.Succeeded, machineId, null);

    public static LocalMachineWriteAttempt NotFound() => new(LocalMachineWriteOutcome.NotFound, null, null);

    public static LocalMachineWriteAttempt Refused(string refusal) =>
        new(LocalMachineWriteOutcome.Refused, null, refusal);
}
