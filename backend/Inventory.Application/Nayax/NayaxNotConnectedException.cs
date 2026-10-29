using Inventory.Domain.Nayax;
using Inventory.Domain.Subscriptions;

namespace Inventory.Application.Nayax;

/// <summary>
/// The stable "Nayax is not usable for this business" failure (issue #520, extended by issue #967):
/// the current business has no Nayax credentials an ordinary operation may use.
///
/// One exception type for every way that can happen, because the consumer's answer is the same in
/// all of them - this feature cannot be served, retrying will not help, and everything local is
/// unaffected. There are two <em>reasons</em>, and they are deliberately distinguishable:
///
/// <list type="bullet">
///   <item><b>The connection.</b> No connection record at all,
///   <see cref="NayaxConnectionStatus.NotConfigured"/>,
///   <see cref="NayaxConnectionStatus.NeedsAttention"/>, and a 401 that has just moved the connection
///   there. <see cref="Status"/> carries the status that refused it and
///   <see cref="RequiredPlan"/> is <see langword="null"/>. An operator has to connect or reconnect
///   Nayax.</item>
///   <item><b>The plan</b> (issue #967). The business's effective plan does not include the sales
///   provider, so the credential gate refuses before the stored connection is read and before any
///   token could be decrypted. <see cref="RequiredPlan"/> carries the plan that would include it and
///   <see cref="Status"/> is <see langword="null"/>, because no connection was read to have one - the
///   business's saved credentials and their status are untouched and stay exactly as they were.</item>
/// </list>
///
/// The frontend shows the exception's own message as it arrives, <see cref="ErrorCode"/> is the stable code
/// it may branch on, and <c>InventoryApi.Http.NayaxConnectionExceptionHandler</c> maps all of it onto
/// the HTTP boundary - including the distinct plan reason, so a screen can say "needs Pro" rather
/// than "connect Nayax" (issue #979).
///
/// Neither reason is a secret: a status is the business's own configuration state and a required plan
/// is what it would have to buy. The message carries no operator id, no token and no business
/// identifier.
/// </summary>
public sealed class NayaxNotConnectedException : Exception
{
    /// <summary>
    /// The stable code a client may branch on. It is part of the API contract: change it only
    /// deliberately, with the frontend. It is deliberately the <em>same</em> code for both reasons,
    /// so a client that has not learnt about plans yet keeps degrading exactly as it does today, and
    /// <see cref="UnavailableReason"/> is what distinguishes them.
    /// </summary>
    public const string ErrorCode = "nayax_not_connected";

    /// <summary>The stable, caller-safe sentence a connection failure carries.</summary>
    public const string StableMessage =
        "Nayax is not connected. Connect this business's Nayax account to use this feature.";

    /// <summary>
    /// The published reason code for a refusal the connection caused, which is every refusal there
    /// was before issue #967. Spelled exactly as every per-feature availability vocabulary spells
    /// the same answer.
    /// </summary>
    public const string ConnectionUnavailableReason = "NayaxNotConnected";

    /// <summary>
    /// The published reason code for a refusal the business's plan caused (issue #967). Spelled
    /// exactly as the per-feature availability vocabularies spell it, so a client meets one word for
    /// one answer.
    /// </summary>
    public const string PlanRequiredReason = "PlanRequired";

    /// <param name="status">The connection status that refused the operation.</param>
    public NayaxNotConnectedException(NayaxConnectionStatus status)
        : base(StableMessage)
    {
        Status = status;
    }

    private NayaxNotConnectedException(SubscriptionPlan requiredPlan)
        : base(PlanRequiredMessage(requiredPlan))
    {
        RequiredPlan = requiredPlan;
    }

    /// <summary>
    /// The refusal the credential gate makes for a business whose plan does not include the sales
    /// provider (issue #967). Nothing about the stored connection is read, so nothing about it is
    /// reported.
    /// </summary>
    /// <param name="requiredPlan">The least plan that includes the sales provider.</param>
    public static NayaxNotConnectedException PlanRequired(SubscriptionPlan requiredPlan) =>
        new(requiredPlan);

    /// <summary>
    /// The status the gate refused, or <see cref="NayaxConnectionStatus.NotConfigured"/> when there
    /// is no stored connection to have a status - and <see langword="null"/> when the refusal was the
    /// plan, because then no connection was read at all.
    /// </summary>
    public NayaxConnectionStatus? Status { get; }

    /// <summary>
    /// The plan that would include the sales provider, for a refusal the business's plan caused, and
    /// <see langword="null"/> for every connection refusal.
    /// </summary>
    public SubscriptionPlan? RequiredPlan { get; }

    /// <summary>
    /// Which of the two reasons this refusal is, as the published code a client branches on.
    /// </summary>
    public string UnavailableReason =>
        RequiredPlan is null ? ConnectionUnavailableReason : PlanRequiredReason;

    /// <summary>
    /// The caller-safe sentence a plan refusal carries. It names the plan rather than the feature,
    /// because one refusal covers every ordinary Nayax call in the application, and it says the
    /// saved credentials are still there, because that is the question a downgraded business asks
    /// first.
    /// </summary>
    public static string PlanRequiredMessage(SubscriptionPlan requiredPlan) =>
        $"This business's plan does not include the Nayax connection. The {requiredPlan} plan "
            + "includes it. Any credentials this business has saved are unchanged.";
}
