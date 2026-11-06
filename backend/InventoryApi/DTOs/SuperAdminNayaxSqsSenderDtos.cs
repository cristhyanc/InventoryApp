using System.Text.Json.Serialization;
using Inventory.Application.PlatformNayaxSqs;

namespace InventoryApi.DTOs;

/// <summary>
/// What a Super Admin submits to
/// <c>PUT /api/super-admin/businesses/{businessId}/nayax-sqs-sender</c> (issue #1101): the AWS IAM
/// user id whose SQS messages belong to this business, whether the mapping is in force, and the
/// revision the caller read.
///
/// <para><strong>It carries no business id and no AWS credential.</strong> The business is the
/// route's subject, and the sender id is an identifier AWS publishes about an IAM user - an access
/// key, a secret or a session token is not this value and has no field here or anywhere else in this
/// application.</para>
/// </summary>
/// <param name="AwsSenderId">
/// The IAM user id, which the server trims and otherwise stores exactly as entered. It must be 16 to
/// 128 letters and digits; anything else is refused with <c>400</c>. <c>null</c> or blank, together
/// with <c>isEnabled: false</c>, is <strong>Remove</strong>: the recorded id is cleared and becomes
/// free for another business.
/// </param>
/// <param name="IsEnabled">
/// Whether the mapping is in force. <c>true</c> with no sender id is refused with <c>400</c>, because
/// a mapping switched on with nothing to match on would claim the queue is attributed when it is not.
/// Marked <see cref="JsonRequiredAttribute"/> so a body that omits it is refused rather than silently
/// bound to <c>false</c> - which would turn an under-posted "change the id" request into a silent
/// disable.
/// </param>
/// <param name="ExpectedRevision">
/// The revision the caller read, and <c>null</c> to create the first row. Marked
/// <see cref="JsonRequiredAttribute"/> so a body that omits it is refused rather than silently
/// treated as "create" - which would make every omitted revision an attempt to create a first row
/// and defeat the concurrency check the field exists for. Anything that is not the stored revision is
/// <c>409</c>, which covers <c>null</c> when a row already exists and a number when none does.
/// </param>
public sealed record SuperAdminNayaxSqsSenderRequest(
    string? AwsSenderId,
    [property: JsonRequired] bool IsEnabled,
    [property: JsonRequired] int? ExpectedRevision);

/// <summary>
/// One business's Nayax SQS sender identity as this surface reports it (issue #1101).
///
/// <para>It carries the business id for the reason the plan-grant response does - a platform
/// decision names the business it is about - and nothing else about the business: no name, contact
/// detail, member, plan or Nayax Lynx credential value. There is no field for any other business,
/// because this surface never reads one.</para>
/// </summary>
/// <param name="BusinessId">The business the sender belongs to.</param>
/// <param name="AwsSenderId">
/// The recorded IAM user id exactly as stored, or <c>null</c> when none is recorded - a row that has
/// never had one, or one a removal cleared.
/// </param>
/// <param name="IsEnabled">Whether the mapping is in force.</param>
/// <param name="LastMessageReceivedAtUtc">
/// When a message from this sender was last accepted, or <c>null</c> for one that has never sent
/// anything. Written by the queue reader of issue #1103; this surface only reports it.
/// </param>
/// <param name="Revision">
/// The revision to present on the next write. A client that holds any other value is refused with
/// <c>409</c>.
/// </param>
/// <param name="CreatedAtUtc">When the row was created.</param>
/// <param name="UpdatedAtUtc">When it was last written.</param>
public sealed record SuperAdminNayaxSqsSenderResponse(
    int BusinessId,
    string? AwsSenderId,
    bool IsEnabled,
    DateTime? LastMessageReceivedAtUtc,
    int Revision,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc)
{
    /// <summary>
    /// Projects the use case's answer. Every judgement on it was made by the use case and the Domain
    /// rules behind it; this mapper only renames.
    /// </summary>
    public static SuperAdminNayaxSqsSenderResponse From(BusinessNayaxSqsSenderView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        return new SuperAdminNayaxSqsSenderResponse(
            view.BusinessId,
            view.Sender.AwsSenderId,
            view.Sender.IsEnabled,
            view.Sender.LastMessageReceivedAtUtc,
            view.Sender.Revision,
            view.Sender.CreatedAtUtc,
            view.Sender.UpdatedAtUtc);
    }
}
