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
///
/// <para><see cref="CanDelete"/> and <see cref="DeletionRefusal"/> are the deletion rule's own answer
/// (issue #896), published exactly as a product's are (issues #737 and #825) and from the one
/// backend-owned policy, <c>Inventory.Domain.Machines.MachineDeletionPolicy</c>. They still publish
/// no provider identifier: a provider-named machine's refusal names the <em>provider</em>, never its
/// id for the machine.</para>
/// </summary>
/// <param name="Id">The machine's own key, assigned by the database.</param>
/// <param name="Name">The operator's own label for the machine.</param>
/// <param name="IsActive">Whether the machine is in service.</param>
/// <param name="IsProviderNamed">Whether the business's sales provider names this machine.</param>
/// <param name="CanDelete">
/// Whether <c>DELETE /api/local-machines/{id}</c> would allow this machine to be deleted. <c>null</c>
/// on a response that did not resolve the question, which is not permission - a screen that offers
/// Delete only on a plain <c>true</c> never offers one that must fail.
/// </param>
/// <param name="DeletionRefusal">
/// Why the machine may not be deleted, in the server's own words - the sentence the delete would
/// answer <c>400</c> with. <c>null</c> when the machine may be deleted, and when the read did not
/// resolve the question.
/// </param>
public record LocalMachineResponse(
    long Id,
    string Name,
    bool IsActive,
    bool IsProviderNamed,
    bool? CanDelete = null,
    string? DeletionRefusal = null);
