namespace Inventory.Application.Time;

/// <summary>
/// The mutable per-request implementation of <see cref="IBusinessTimeZoneProvider"/> (issue #499),
/// built on the same rule as <c>BusinessScope</c>: it starts unresolved and can only ever be moved
/// forward to a zone, once.
///
/// That ordering is the safety property. A request that has not been through business resolution,
/// or that failed it, has no business zone, so nothing can derive a business date for it; and
/// nothing later in the request can quietly repoint an already-resolved request at a different
/// business's calendar.
/// </summary>
public sealed class BusinessTimeZoneScope : IBusinessTimeZoneProvider
{
    public string? TimeZoneId { get; private set; }

    /// <summary>
    /// Publishes the current business's IANA time zone for the rest of the request. Called once,
    /// by the API boundary, after membership resolution has succeeded.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="timeZoneId"/> is null or blank.</exception>
    /// <exception cref="InvalidOperationException">A zone has already been published.</exception>
    public void Resolve(string timeZoneId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(timeZoneId);

        if (TimeZoneId is not null)
        {
            throw new InvalidOperationException(
                "The current business's time zone has already been resolved for this scope and cannot be changed.");
        }

        TimeZoneId = timeZoneId;
    }
}
