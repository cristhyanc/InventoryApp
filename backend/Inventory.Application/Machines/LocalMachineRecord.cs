namespace Inventory.Application.Machines;

/// <summary>
/// A persisted machine as seen by the Application layer (issue #740, parent #329 child G1). It is
/// the business's own record of a machine and exists with or without a sales provider, which is what
/// makes it a different thing from <see cref="MachineSummary"/> - that one is the live provider
/// fleet with this business's sales and costs resolved onto it.
///
/// <para><paramref name="IsProviderNamed"/> is whether the business's sales provider names this
/// machine - that is, whether the row carries an <c>ExternalMachineId</c>. It is the fact and not
/// the identifier, exactly as on a product or a category: a provider's id is integration state and
/// is never published, and nothing a caller submits can set or clear it.</para>
/// </summary>
public sealed record LocalMachineRecord(long Id, string Name, bool IsActive, bool IsProviderNamed = false);
