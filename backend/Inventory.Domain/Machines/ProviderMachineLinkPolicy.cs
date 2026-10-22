namespace Inventory.Domain.Machines;

/// <summary>
/// What the operator asked the review to do with one provider machine no local machine carries the
/// id of (issue #742, parent #329 child G3). Numbered from 1, so an unwritten integer is not an
/// action - the same shape as <c>Inventory.Domain.Providers.CatalogueReviewAction</c>.
/// </summary>
public enum ProviderMachineLinkAction
{
    /// <summary>
    /// Leave the provider machine alone this time. The honest answer for one the operator has not
    /// decided about yet, and the default for every entry no decision names.
    /// </summary>
    Defer = 1,

    /// <summary>Record the provider's machine id on a machine of this business the operator named.</summary>
    LinkExisting = 2,

    /// <summary>Create a new local machine carrying the provider's machine id and its name.</summary>
    CreateNew = 3,
}

/// <summary>
/// The deterministic rules of provider machine linking (issue #742, parent #329 child G3): when a
/// provider machine may be linked without asking, when a link may be recorded at all, and what a
/// machine created for one is called.
///
/// <para><strong>Nothing is matched by name.</strong> A provider machine resolves to a local machine
/// only through that machine's <c>ExternalMachineId</c>. A local machine called exactly what the
/// provider calls its machine is a different machine, and a local id that happens to equal a provider
/// machine number is a coincidence (AGENTS.md § Database and migrations, issue #735). The only ways a
/// link comes about are the operator naming a machine and a machine this feature creates.</para>
///
/// <para>It holds no clock, no database and no configuration: every answer is a function of its
/// arguments, so the review, the apply, the adapter that writes the link and a test all decide
/// identically.</para>
/// </summary>
public static class ProviderMachineLinkPolicy
{
    /// <summary>
    /// Whether provider machines this business carries no id for may be linked <em>automatically</em>,
    /// as new local machines, rather than waiting for the operator to decide about each one.
    ///
    /// <para>They may only when the business has no machine of its own without a provider id. The
    /// reason is the mistake the alternative makes: a business that recorded its machines by hand
    /// before connecting a provider has exactly the machines the provider is about to describe, so
    /// creating a second row for each of them would duplicate its own fleet. With nothing unlinked
    /// there is no local machine a new provider machine could be the same machine as, so creating one
    /// invents nothing and asks nobody a question with one possible answer.</para>
    ///
    /// <para>A deactivated machine without a provider id counts: it is still a machine of this
    /// business that the provider machine may be, and a retired row is better linked than
    /// duplicated.</para>
    /// </summary>
    public static bool MayLinkAutomatically(bool anyLocalMachineWithoutProviderId) =>
        !anyLocalMachineWithoutProviderId;

    /// <summary>
    /// Why a link decision is not usable at all, or <see langword="null"/> when it is. These are
    /// statements about the decision itself rather than about stored rows, so they are answered before
    /// anything is read.
    /// </summary>
    public static string? DecisionRefusal(ProviderMachineLinkAction action, long? localMachineId)
    {
        if (action == ProviderMachineLinkAction.LinkExisting && localMachineId is null)
            return "linking to one of this business's machines needs that machine named. Nothing was changed.";

        if (action != ProviderMachineLinkAction.LinkExisting && localMachineId is not null)
            return "a machine of this business may only be named when linking to it. Nothing was changed.";

        return null;
    }

    /// <summary>
    /// Why a link may not be recorded, or <see langword="null"/> when it may.
    ///
    /// <para>Both refusals are statements about <em>stored</em> rows, so they are decided in the same
    /// transaction as the write that would follow them, never from a value read earlier. The second is
    /// what makes "one business cannot link two machines to the same provider machine id" an answer
    /// rather than a database error: the filtered unique index on
    /// <c>(BusinessId, ExternalMachineId)</c> refuses it too, and this says so first.</para>
    ///
    /// <para>Re-recording the id a machine already carries is <strong>not</strong> a refusal: it is the
    /// state the apply was asked to reach, which is what makes applying the same decision twice a
    /// no-op.</para>
    /// </summary>
    /// <param name="providerMachineId">The provider machine id the link would record.</param>
    /// <param name="chosenMachineProviderId">The provider id the chosen local machine carries today, if any.</param>
    /// <param name="anotherMachineCarriesIt">Whether a different machine of this business already carries <paramref name="providerMachineId"/>.</param>
    public static string? LinkRefusal(
        string providerMachineId, string? chosenMachineProviderId, bool anotherMachineCarriesIt)
    {
        ArgumentNullException.ThrowIfNull(providerMachineId);

        if (chosenMachineProviderId is not null
            && !string.Equals(chosenMachineProviderId, providerMachineId, StringComparison.Ordinal))
            return $"This machine is already linked to provider machine {chosenMachineProviderId}, so it "
                + $"cannot also be linked to {providerMachineId}. Nothing was changed.";

        if (anotherMachineCarriesIt)
            return $"Provider machine {providerMachineId} is already linked to a different machine of this "
                + "business, so it cannot be linked again. Nothing was changed.";

        return null;
    }

    /// <summary>
    /// What a local machine created for a provider machine is called: the provider's own name for it,
    /// trimmed, and <see cref="MachineCataloguePolicy.DefaultName"/> - "Machine" and the provider's id
    /// - when the provider names it nothing.
    ///
    /// <para>The fallback is the shape the <c>AddMachines</c> backfill already writes for a machine
    /// nothing had named, so a machine linked later is named the same way as one linked then. The
    /// authoritative Lynx contract for <c>GET /v1/machines</c> declares <c>MachineName</c> as
    /// <c>string | null</c> (Nayax developer portal, Get All Machine Basic Info), so an unnamed machine
    /// is a state the provider really has rather than one this application invented a case for.</para>
    /// </summary>
    public static string LinkedMachineName(string? providerMachineName, string externalMachineId)
    {
        ArgumentNullException.ThrowIfNull(externalMachineId);

        return string.IsNullOrWhiteSpace(providerMachineName)
            ? MachineCataloguePolicy.DefaultName(externalMachineId)
            : MachineCataloguePolicy.Normalize(providerMachineName);
    }
}
