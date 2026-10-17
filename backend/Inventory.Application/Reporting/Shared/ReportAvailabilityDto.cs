using Inventory.Domain.Reporting.Availability;

namespace Inventory.Application.Reporting.Shared;

/// <summary>
/// The availability of one family of provider-sourced report figures, as the API reports it
/// (issue #730, parent #329 decision P9).
///
/// <para><see cref="State"/> is the <see cref="ReportAvailabilityState"/> name - <c>Complete</c>,
/// <c>Stale</c>, <c>Unverified</c> or <c>Missing</c> - rather than its number, because this is a
/// contract a client reads and a renumbered enum must not silently change what a stored or cached
/// response meant. Only <c>Complete</c> permits a figure's zero to be presented as a real zero; in
/// every other state a presentation shows the state instead of a zero (AGENTS.md § Frontend rules:
/// preserve nullable financial values and display incomplete states).</para>
/// </summary>
/// <param name="State">The decided state's name.</param>
/// <param name="AsOfUtc">
/// For <c>Stale</c>, the UTC instant the figures are complete up to. Null otherwise.
/// </param>
public sealed record ReportAvailabilityDto(string State, DateTime? AsOfUtc)
{
    /// <summary>Whether a zero in the figures this governs is a real zero.</summary>
    public bool IsComplete => State == nameof(ReportAvailabilityState.Complete);

    public static ReportAvailabilityDto From(ReportAvailability availability) =>
        new(availability.State.ToString(), availability.AsOfUtc);

    /// <summary>
    /// The conservative default an unset availability carries. It is deliberately not
    /// <c>Complete</c>: a state nothing decided must never license presenting a zero as a real zero.
    /// </summary>
    public static ReportAvailabilityDto Undecided { get; } =
        new(nameof(ReportAvailabilityState.Unverified), null);
}

/// <summary>
/// The availability of a report's provider-sourced figures, split by the data each one actually
/// comes from, so a figure is never labelled by evidence that does not feed it.
/// </summary>
/// <param name="Sales">
/// Governs every figure derived from stored provider sales: gross sales, the card/cash split and
/// their transaction counts, COGS and the profit figures built on it, estimated GST on sales, site
/// commission and the net card settlement.
/// </param>
/// <param name="Fees">
/// Governs every figure derived from imported settlement data: the Nayax processing fees excluding
/// and including GST, and the GST on those fees.
/// </param>
public sealed record ReportProviderAvailabilityDto(ReportAvailabilityDto Sales, ReportAvailabilityDto Fees)
{
    public static ReportProviderAvailabilityDto Undecided { get; } =
        new(ReportAvailabilityDto.Undecided, ReportAvailabilityDto.Undecided);
}
