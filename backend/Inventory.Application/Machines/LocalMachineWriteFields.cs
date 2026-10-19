namespace Inventory.Application.Machines;

/// <summary>
/// A machine's editable fields, as seen by the Application layer (issue #740). A machine has exactly
/// one: what it is called. Its activation is changed by its own two operations, and its provider id
/// is written only by provider synchronization or the provider machine review (issue #742) - never
/// through this use case, and never from anything a request carries.
/// </summary>
public sealed record LocalMachineWriteFields(string? Name);
