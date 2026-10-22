using Inventory.Domain.Machines;

namespace Inventory.Application.Machines;

/// <summary>
/// One machine of the business's sales provider, as the review reports it (issue #742, parent #329
/// child G3).
///
/// <para><paramref name="Name"/> is what the provider calls the machine, or
/// <c>Machine {ProviderMachineId}</c> when the provider names it nothing
/// (<see cref="ProviderMachineLinkPolicy.LinkedMachineName"/>) - the name a machine created for it
/// would carry.</para>
///
/// <para><paramref name="LocalMachineId"/> is the machine of this business carrying this provider
/// machine's id, or <c>null</c> when none does - the entry the operator links to a machine of theirs
/// or creates a new one for. It is resolved through <c>LocalMachine.ExternalMachineId</c> alone:
/// nothing is matched by name, and a local id equal to a provider machine number is a
/// coincidence.</para>
/// </summary>
public sealed record ProviderMachineEntry(
    long ProviderMachineId,
    string Name,
    string? ProviderMachineNumber,
    long? LocalMachineId,
    string? LocalMachineName)
{
    /// <summary>Whether a machine of this business already carries this provider machine's id.</summary>
    public bool IsLinked => LocalMachineId.HasValue;
}

/// <summary>
/// One link that was recorded: the provider machine, the machine of this business that now carries
/// its id, and whether that machine is new. <paramref name="Created"/> is the difference between a
/// machine this feature added and one the operator already had.
/// </summary>
public sealed record ProviderMachineLinked(
    long ProviderMachineId,
    long LocalMachineId,
    string Name,
    bool Created);

/// <summary>
/// A reading of the business's provider fleet against its own machines (issue #742, parent #329
/// child G3).
///
/// <para>It is the one place the two outcomes of that reading are reported.
/// <paramref name="AutoLinked"/> is what this read linked <em>by itself</em>, as new local machines,
/// which it does only for a business with no machine of its own lacking a provider id
/// (<see cref="ProviderMachineLinkPolicy.MayLinkAutomatically"/>).
/// <paramref name="AwaitingReview"/> is what it deliberately did not link: the provider machines the
/// operator has to decide about, because any of them may be a machine they already recorded by
/// hand.</para>
///
/// <para><paramref name="Linked"/> is every provider machine that resolves to a machine of this
/// business after this read, including the ones it just linked. A provider machine stays in the
/// provider's own pages whether it is linked or not: until it is, its sales, stock events and reports
/// are kept under its raw provider id exactly as before.</para>
/// </summary>
public sealed record ProviderMachineReview(
    DateTime ReviewedAtUtc,
    IReadOnlyList<ProviderMachineLinked> AutoLinked,
    IReadOnlyList<ProviderMachineEntry> AwaitingReview,
    IReadOnlyList<ProviderMachineEntry> Linked)
{
    /// <summary>Provider machines the operator still has to decide about.</summary>
    public int AwaitingReviewCount => AwaitingReview.Count;

    /// <summary>Provider machines a machine of this business carries the id of.</summary>
    public int LinkedCount => Linked.Count;
}

/// <summary>
/// What the operator decided about one provider machine.
///
/// <para>It carries no provider name and no provider machine number: every value a link writes comes
/// from the fleet read the server makes for the apply itself, so nothing a caller submits reaches a
/// machine's name or its provider id. The one thing the request chooses is <em>which</em> provider
/// machine, and which machine of the business to link it to.</para>
///
/// <para>An entry no decision names is deferred, which is the honest default: a review the operator
/// has only half decided must not link the other half by omission.</para>
/// </summary>
public sealed record ProviderMachineLinkDecision(
    long ProviderMachineId,
    ProviderMachineLinkAction Action = ProviderMachineLinkAction.Defer,
    long? LocalMachineId = null);

/// <summary>
/// The request that applies provider machine links. It carries decisions and nothing else.
/// </summary>
public sealed record ApplyProviderMachineLinksRequest(
    IReadOnlyList<ProviderMachineLinkDecision>? Decisions = null);

/// <summary>What one apply recorded (issue #742). One refused decision refuses all of them.</summary>
public sealed record ProviderMachineLinksApplied(
    DateTime AppliedAtUtc,
    IReadOnlyList<ProviderMachineLinked> Links)
{
    /// <summary>Machines this apply created.</summary>
    public int CreatedCount => Links.Count(link => link.Created);

    /// <summary>Machines the business already had that this apply linked.</summary>
    public int LinkedCount => Links.Count(link => !link.Created);
}
