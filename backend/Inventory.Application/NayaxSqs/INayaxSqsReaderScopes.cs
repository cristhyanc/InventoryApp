namespace Inventory.Application.NayaxSqs;

/// <summary>
/// One service scope whose current business is the business a message was attributed to (issue
/// #1103), and the only thing the queue reader may do inside it.
///
/// <para>Disposing it ends that scope, and with it the <c>AppDbContext</c> everything in it read and
/// wrote - exactly as the end of a request does. One message is one scope: a message for business A
/// can therefore never reuse the context, the credential read, the plan answer or the change tracker
/// of a message for business B.</para>
/// </summary>
public interface INayaxSqsBusinessWorkScope : IAsyncDisposable
{
    /// <summary>
    /// Runs <see cref="ProcessNayaxSqsSale"/> for this message inside that business's scope, and
    /// answers what the reader should do with the message.
    /// </summary>
    Task<NayaxSqsProcessingOutcome> ProcessAsync(
        string messageId, string? messageBody, CancellationToken cancellationToken);
}

/// <summary>
/// The two service scopes the Nayax SQS sales reader needs, behind one port so its loop stays free of
/// the hosting container (issue #1103).
///
/// <para><strong>They are two scopes and not one, and the difference is the whole tenancy
/// story.</strong> Attributing a message happens in a scope that has resolved <em>no</em> business -
/// which is what makes the sender lookup a reviewed <c>IgnoreQueryFilters</c> statement rather than
/// something a tenant filter could answer - and everything a message then causes happens in a scope
/// resolved to the one business that lookup named, where the ordinary filters and
/// <c>BusinessOwnershipEnforcer</c> are fully in force. Neither scope outlives the message it was
/// opened for.</para>
///
/// <para><strong>The business id comes from the sender directory's answer and from nowhere
/// else.</strong> Nothing in a message chooses it: the SQS <c>SenderId</c> system attribute is
/// matched against the mapping a Super Admin recorded (issue #1101), and the key of the row that
/// matched is what is published as the second scope's current business. That is the same shape as
/// <c>ConnectedMachineSnapshotCommand</c>'s per-business recorder, where the id comes from the
/// business row the enumeration read.</para>
/// </summary>
public interface INayaxSqsReaderScopes
{
    /// <summary>
    /// Opens a scope that has resolved no business, asks
    /// <see cref="INayaxSqsSenderDirectory.FindBusinessBySenderAsync"/> whose sender
    /// <paramref name="senderId"/> is, and closes it again.
    /// </summary>
    Task<NayaxSqsSenderAttribution?> FindBusinessBySenderAsync(
        string senderId, CancellationToken cancellationToken);

    /// <summary>Opens a scope resolved to <paramref name="businessId"/>.</summary>
    INayaxSqsBusinessWorkScope CreateForBusiness(int businessId);
}
