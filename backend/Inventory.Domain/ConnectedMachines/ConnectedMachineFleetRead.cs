using System.Globalization;

namespace Inventory.Domain.ConnectedMachines;

/// <summary>
/// One read of a business's Nayax fleet, as the connected-machine count is taken from it (issue
/// #978, child of #965).
///
/// <para><strong>A connected machine is a machine on the business's Nayax account</strong>, linked in
/// this application or not - the decision of 29 October 2026 on #965, because Nayax imports sales for
/// every machine on the account whether this application has linked it or not. So the count comes
/// from the fleet read and from nothing local: a <em>manual</em> machine has no provider id at all, is
/// never in a fleet read, and therefore can never be counted. Nothing here reads
/// <c>LocalMachine</c>.</para>
///
/// <para><strong>Every machine the fleet read returns is counted.</strong> The Lynx fleet contract
/// (<c>GET /v1/machines</c>, "Get All Machine Basic Info") does publish a
/// <c>MachineStatusBit</c>, declared as "a bit field indicating the status of the machine" - but the
/// only place the Nayax developer portal gives that field a <em>meaning</em> is the create-machine
/// guide, as a request value ("1 for active, 2 for inactive"), and nothing documents what a read
/// returns for a machine that was removed. Under the acceptance criterion of #978 an inactive or
/// removed machine may be excluded "only after confirming from the actual fleet endpoint's response
/// and the Nayax docs that the field is returned and what it means"; that confirmation is not
/// available for a read, so this type counts every machine returned and the application does not
/// deserialise the field at all. Excluding one later is a deliberate change with the then-confirmed
/// contract behind it (AGENTS.md § Nayax contract verification).</para>
///
/// <para><strong>An incomplete read is not a count.</strong> A failed, partial or
/// paginated-incomplete read answers <see cref="Incomplete"/>, and no snapshot is ever stored from
/// one: a fleet nobody finished reading is not a fleet with fewer machines in it. The whole-fleet
/// call this application makes passes no <c>ResultsLimit</c>, which the Lynx contract documents as
/// "leave it out to get all machines", so a successful whole-fleet read is complete.</para>
/// </summary>
public sealed class ConnectedMachineFleetRead
{
    private ConnectedMachineFleetRead(IReadOnlyList<long> providerMachineIds, bool isComplete)
    {
        ProviderMachineIds = providerMachineIds;
        IsComplete = isComplete;
    }

    /// <summary>
    /// The provider machine ids this read saw: distinct, positive and in ascending order, so the
    /// same fleet always produces the same recorded evidence whatever order Nayax answered in.
    ///
    /// <para>An id that is absent or not positive identifies no machine, so it is not counted. That
    /// is visible rather than silent: the ids counted are stored with the count, so a count and the
    /// machines behind it can always be compared.</para>
    /// </summary>
    public IReadOnlyList<long> ProviderMachineIds { get; }

    /// <summary>
    /// Whether this read accounted for the whole fleet. Only a complete read may become a snapshot.
    /// </summary>
    public bool IsComplete { get; }

    /// <summary>How many connected machines this read saw.</summary>
    public int Count => ProviderMachineIds.Count;

    /// <summary>A read that accounted for the whole fleet.</summary>
    public static ConnectedMachineFleetRead Complete(IEnumerable<long?> providerMachineIds) =>
        new(Normalize(providerMachineIds), isComplete: true);

    /// <summary>
    /// A read that did not account for the whole fleet - it failed part way, one page was refused, or
    /// the caller could not tell. It carries whatever it saw, and it never becomes a snapshot.
    /// </summary>
    public static ConnectedMachineFleetRead Incomplete(IEnumerable<long?> providerMachineIds) =>
        new(Normalize(providerMachineIds), isComplete: false);

    /// <summary>
    /// The canonical stored form of <see cref="ProviderMachineIds"/>: the ids as invariant decimal
    /// text, comma separated, in the order above.
    ///
    /// <para>It is stated once, here, because the stored evidence and anything that reads it back
    /// have to agree on the format exactly - a billing question answered months later is answered
    /// from this text.</para>
    /// </summary>
    public string FormatProviderMachineIds() => FormatIds(ProviderMachineIds);

    /// <summary>Formats a list of provider machine ids in the stored form above.</summary>
    public static string FormatIds(IEnumerable<long> providerMachineIds)
    {
        ArgumentNullException.ThrowIfNull(providerMachineIds);

        return string.Join(
            ",",
            providerMachineIds.Select(id => id.ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>
    /// Reads the stored form above back into ids. An empty or absent value is no machines, which is
    /// a real count of zero rather than an unknown one.
    /// </summary>
    /// <exception cref="FormatException">
    /// The text holds something that is not an id. A stored snapshot is billing evidence, so an
    /// unreadable one fails rather than being reported as a smaller fleet.
    /// </exception>
    public static IReadOnlyList<long> ParseIds(string? storedIds)
    {
        if (string.IsNullOrWhiteSpace(storedIds))
        {
            return [];
        }

        return
        [
            .. storedIds
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => long.Parse(part, CultureInfo.InvariantCulture)),
        ];
    }

    private static IReadOnlyList<long> Normalize(IEnumerable<long?> providerMachineIds)
    {
        ArgumentNullException.ThrowIfNull(providerMachineIds);

        return
        [
            .. providerMachineIds
                .Where(id => id is > 0)
                .Select(id => id!.Value)
                .Distinct()
                .Order(),
        ];
    }
}
