namespace InventoryApi.DTOs;

/// <summary>
/// The machine a client posts to <c>POST /api/local-machines</c> or puts to
/// <c>PUT /api/local-machines/{id}</c> (issue #740, parent #329 child G1).
///
/// <para>A machine is name-only, so the request carries exactly one field. Its activation is changed
/// by its own two endpoints, and its provider id is <strong>never</strong> on the wire in either
/// direction: that column is written by the <c>AddMachines</c> backfill and, later, by provider
/// synchronization and the provider machine review (issue #742).</para>
/// </summary>
public record LocalMachineDto(string Name);

/// <summary>
/// The wire shape of one of the business's own machine records, on <c>/api/local-machines</c>.
///
/// <para><see cref="IsProviderNamed"/> says whether the business's sales provider names this machine
/// - the fact, never the identifier - so a client can tell a machine that came from the fleet apart
/// from one somebody typed in. It is not editable, and no request field corresponds to it.</para>
/// </summary>
public record LocalMachineResponse(long Id, string Name, bool IsActive, bool IsProviderNamed);
