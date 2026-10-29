namespace Inventory.Domain.Subscriptions;

/// <summary>
/// What one plan costs and what it includes (issue #966, from the agreed proposal of #965).
///
/// <para><strong>Money is an integer count of minor units, with its currency beside it.</strong>
/// A$7.00 is <c>700</c> and <c>"AUD"</c>, never <c>7.0</c>: a price that reaches a payment provider,
/// an invoice and a page must have exactly one representation, and a binary floating-point price is
/// the one kind of money that cannot be compared for equality. A decimal would carry the same value
/// honestly, but every billing provider this plan will be charged through counts minor units, so
/// counting them here is what keeps the conversion in one place - at the boundary, not in the
/// catalogue.</para>
///
/// <para><strong>Whether these prices include GST is an open launch decision</strong> (#965 §
/// "Launch decisions to settle"). The catalogue records the advertised amount agreed on 29 October
/// 2026 and claims nothing about its tax treatment; the billing integration of #969 is where a tax
/// basis is decided and recorded, and no reader here may infer one.</para>
/// </summary>
/// <param name="Plan">The plan this definition is for.</param>
/// <param name="MonthlyPriceMinorUnits">
/// The advertised price per business per month, in minor units of
/// <paramref name="CurrencyCode"/> - cents, for AUD. Zero for <see cref="SubscriptionPlan.Free"/>,
/// which is a real price rather than an absent one.
/// </param>
/// <param name="CurrencyCode">
/// The ISO 4217 code the amounts above and below are in. Explicit rather than assumed, so a figure
/// can never be rendered or charged in a currency nobody stated.
/// </param>
/// <param name="AttachmentStorageLimitBytes">
/// How many bytes of purchase and expense attachments the business may hold, counted together.
/// Transaction records are not attachments and are not counted. The quota's own enforcement - the
/// 80% warning, the 100% block on new uploads only, and never deleting an attachment on a
/// downgrade - is issue #968's work and reads this one number.
/// </param>
/// <param name="IncludedConnectedMachines">
/// How many connected machines the price above includes. A connected machine is a machine on the
/// business's Nayax account, linked in this application or not (decided 29 October 2026); a manual
/// machine is not one. Zero on the plans that have no Nayax connection at all, which is a real
/// allowance of none rather than an unlimited one.
/// </param>
/// <param name="AdditionalConnectedMachinePriceMinorUnits">
/// What each connected machine beyond <paramref name="IncludedConnectedMachines"/> costs per month,
/// in the same minor units, or <see langword="null"/> when the plan offers no connected machines to
/// add to. <strong>Being above the allowance is billed, never blocked</strong> - no code may read
/// this as a limit.
/// </param>
public sealed record PlanDefinition(
    SubscriptionPlan Plan,
    int MonthlyPriceMinorUnits,
    string CurrencyCode,
    long AttachmentStorageLimitBytes,
    int IncludedConnectedMachines,
    int? AdditionalConnectedMachinePriceMinorUnits);

/// <summary>
/// The authoritative Free/Starter/Pro catalogue (issue #966, prices and limits from #965).
///
/// <para><strong>One definition, read by everything.</strong> The plan page, the public pricing, the
/// storage quota of #968, the connected-machine count of #978 and the billing integration of #969
/// all read these rows; none of them restates a price or a limit, for the same reason no
/// presentation restates a financial formula (AGENTS.md § Core bookkeeping and reporting
/// invariants).</para>
///
/// <para>It is deliberately a compiled table rather than configuration or a database row. A price
/// change is a commercial decision with a deployment behind it, and a table a setting could edit is
/// a table that can disagree with what a customer was charged.</para>
/// </summary>
public static class PlanCatalogue
{
    /// <summary>The one currency the catalogue is priced in today.</summary>
    public const string Currency = "AUD";

    /// <summary>
    /// A gigabyte, as the attachment limits below count one: 2^30 bytes. Stated once, because "1 GB"
    /// and "20 GB" have to mean the same multiple everywhere a quota is measured or reported.
    /// </summary>
    private const long Gigabyte = 1024L * 1024L * 1024L;

    private static readonly PlanDefinition FreeDefinition = new(
        SubscriptionPlan.Free,
        MonthlyPriceMinorUnits: 0,
        Currency,
        AttachmentStorageLimitBytes: 1 * Gigabyte,
        IncludedConnectedMachines: 0,
        AdditionalConnectedMachinePriceMinorUnits: null);

    private static readonly PlanDefinition StarterDefinition = new(
        SubscriptionPlan.Starter,
        MonthlyPriceMinorUnits: 700,
        Currency,
        AttachmentStorageLimitBytes: 20 * Gigabyte,
        IncludedConnectedMachines: 0,
        AdditionalConnectedMachinePriceMinorUnits: null);

    private static readonly PlanDefinition ProDefinition = new(
        SubscriptionPlan.Pro,
        MonthlyPriceMinorUnits: 1500,
        Currency,
        AttachmentStorageLimitBytes: 20 * Gigabyte,
        IncludedConnectedMachines: 5,
        AdditionalConnectedMachinePriceMinorUnits: 200);

    /// <summary>Every plan's definition, in <see cref="SubscriptionPlans.All"/>'s order.</summary>
    public static readonly IReadOnlyList<PlanDefinition> All =
    [
        FreeDefinition,
        StarterDefinition,
        ProDefinition,
    ];

    /// <summary>The definition of one plan.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="plan"/> is not declared (see <see cref="SubscriptionPlans.IsDeclared"/>).
    /// There is no catalogue entry to invent for a plan this code does not know, and answering with
    /// one would be answering with a price nobody agreed.
    /// </exception>
    public static PlanDefinition For(SubscriptionPlan plan) =>
        All.FirstOrDefault(definition => definition.Plan == plan)
            ?? throw new ArgumentOutOfRangeException(
                nameof(plan),
                plan,
                "Only a declared plan has a catalogue entry; check SubscriptionPlans.IsDeclared first.");
}
