namespace Inventory.Application.SupportSessions;

/// <summary>
/// The three stable error codes a support request can be refused with (issue #551), written once
/// here because the refusals are produced in three different layers and a client branches on the
/// code rather than on the sentence.
///
/// <para>They are codes rather than messages on purpose: the sentences beside them are for a
/// person, and the code is what the frontend's support banner (issue #513) switches on - "your
/// session is over, start another one", "this view is read-only", "Nayax is not reachable from a
/// support view". A second spelling of any of them would be a second answer a client has to
/// guess between.</para>
/// </summary>
public static class SupportSessionErrorCodes
{
    /// <summary>
    /// The presented session id names no session the caller may still use: it was never theirs, it
    /// was ended, it has expired, or the business it was granted for is no longer active. One code
    /// for all four, deliberately: telling a caller which of them it was would describe somebody
    /// else's session, and in every case the answer is the same - start a new one, which is
    /// audited.
    /// </summary>
    public const string SessionEnded = "support_session_ended";

    /// <summary>
    /// The request would have changed something. A support session grants read access and nothing
    /// else, so this is the answer to every non-<c>GET</c> call in one, and to any write that got
    /// past the method rule and reached the persistence boundary or document storage.
    /// </summary>
    public const string ReadOnly = "read_only_support_session";

    /// <summary>
    /// The request needed the business's Nayax credentials, which a support session never resolves
    /// (issue #551's third criterion). The business's own operator id and token are its own, and a
    /// supporter looking at its data is not a reason to spend them.
    /// </summary>
    public const string NayaxUnavailable = "unavailable_in_support_view";
}

/// <summary>
/// Raised when something in a support session tried to write (issue #551).
///
/// <para>It is a fault rather than a validation message, in exactly the sense
/// <c>CrossBusinessAccessException</c> is: the method rule in the capability authorization handler
/// already refuses every non-<c>GET</c> request in a session, so reaching here means a
/// <c>GET</c> with a side effect found its way to the persistence boundary or to document storage.
/// The whole operation is abandoned, and the message names the rule and never the data.</para>
///
/// <para>It is deliberately not <c>CrossBusinessAccessException</c>: nothing here crossed a
/// business boundary - the write was for the right business - and a reader of a log needs the two
/// told apart.</para>
/// </summary>
public sealed class SupportSessionReadOnlyException : Exception
{
    /// <summary>
    /// The one sentence this refusal is reported with. It names the rule, not the record, the
    /// business or the administrator.
    /// </summary>
    public const string RefusalMessage =
        "A support session is read-only, so this request cannot change anything.";

    public SupportSessionReadOnlyException() : base(RefusalMessage)
    {
    }

    public SupportSessionReadOnlyException(string message) : base(message)
    {
    }

    public SupportSessionReadOnlyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Raised when a Nayax call is attempted in a support session (issue #551).
///
/// <para>The business's Nayax operator id and access token are its own credential
/// (AGENTS.md § Nayax integration). A support session is somebody from outside the business
/// reading its data to help with a problem, and that is not a reason to authenticate to Nayax as
/// the business: the refusal is made where the credentials would be resolved, so it holds for every
/// Nayax read and every Nayax write at once and no request leaves the API.</para>
/// </summary>
public sealed class SupportSessionNayaxUnavailableException : Exception
{
    /// <summary>
    /// The one sentence this refusal is reported with. It names neither the business nor any part
    /// of the credential it declined to resolve.
    /// </summary>
    public const string RefusalMessage =
        "Nayax is not available in a support view, so this request cannot be completed.";

    public SupportSessionNayaxUnavailableException() : base(RefusalMessage)
    {
    }

    public SupportSessionNayaxUnavailableException(string message) : base(message)
    {
    }

    public SupportSessionNayaxUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
