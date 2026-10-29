using Inventory.Domain.Subscriptions;

namespace Inventory.Application.Attachments;

/// <summary>
/// The one translation from "what this business is entitled to" into "how many bytes of
/// attachments it may hold" (issue #968).
///
/// <para>It exists so the upload guard and the storage figures reported by
/// <c>GET /api/me/access</c> cannot answer differently: a business warned at 80% of one number and
/// refused at 100% of another would be a quota nobody could explain.</para>
/// </summary>
public static class AttachmentStorageAllowance
{
    /// <summary>
    /// The allowance the entitlement implies, or <see langword="null"/> when there is none to
    /// report - while nothing is enforced (the shipped state), and for an effective plan this code
    /// does not declare, which is treated as no enforcement rather than as the nearest plan it
    /// knows.
    /// </summary>
    public static long? For(EffectiveEntitlement entitlement)
    {
        ArgumentNullException.ThrowIfNull(entitlement);

        if (!entitlement.Enforced || entitlement.Plan is not { } plan || !SubscriptionPlans.IsDeclared(plan))
            return null;

        return PlanCatalogue.For(plan).AttachmentStorageLimitBytes;
    }
}
