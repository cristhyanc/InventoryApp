using Inventory.Domain.Attachments;
using Inventory.Domain.Subscriptions;

namespace Inventory.Application.Attachments;

/// <summary>
/// The attachment storage figures <c>GET /api/me/access</c> reports beside the entitlement
/// (issue #968): how much of the allowance the business is using, what the allowance is, and which
/// of the three warning states that puts it in.
///
/// <para><strong>It takes the entitlement rather than reading it again.</strong> The access
/// response has already decided what the business is entitled to, and deciding it twice in one
/// request could report a limit the same response contradicts.</para>
///
/// <para><strong>Nothing is read while nothing is enforced.</strong> With no plan being enforced
/// there is no allowance to be a percentage of, so the answer is <see langword="null"/> and no
/// aggregate runs - the same rule issue #966 applies to the subscription records themselves, and
/// what keeps the shipped state's per-request cost unchanged.</para>
///
/// <para>The usage definition and the 80% threshold belong to
/// <see cref="AttachmentStorageQuotaPolicy"/>; this use case only supplies it with facts.</para>
/// </summary>
public sealed class GetAttachmentStorageStatus
{
    private readonly IAttachmentQuotaStore _store;

    public GetAttachmentStorageStatus(IAttachmentQuotaStore store)
    {
        _store = store;
    }

    /// <summary>
    /// The storage state for the business this request acts in, or <see langword="null"/> when no
    /// plan is enforced for it.
    /// </summary>
    public async Task<AttachmentStorageStatus?> Handle(
        EffectiveEntitlement entitlement, CancellationToken cancellationToken = default)
    {
        if (AttachmentStorageAllowance.For(entitlement) is not { } limitBytes) return null;

        var usage = await _store.ReadUsageAsync(cancellationToken).ConfigureAwait(false);

        return new AttachmentStorageStatus(
            usage.TotalBytes,
            limitBytes,
            AttachmentStorageQuotaPolicy.WarningLevelFor(usage.TotalBytes, limitBytes));
    }
}
