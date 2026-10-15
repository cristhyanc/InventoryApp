namespace Inventory.Domain.Nayax;

/// <summary>
/// What a Nayax operator id is allowed to be (issue #681).
///
/// <para><strong>It is a number, and that is Nayax's own contract, not an assumption.</strong> The
/// Lynx operator endpoints declare their path parameter as
/// <c>OperatorID: integer&lt;int64&gt;, required</c> - see
/// <c>GET /v1/operators/{OperatorID}/products</c> and
/// <c>GET /v1/operators/{OperatorID}/productGroups</c> in the Nayax developer portal - and Nayax Core
/// publishes the value as "your numeric Operator ID". So a stored or submitted operator id that is
/// not a positive whole number is not a value Nayax could ever answer for.</para>
///
/// <para><strong>Why it is checked at all.</strong> The operator id is interpolated into a request
/// path, so an id carrying <c>/</c>, <c>?</c>, <c>#</c> or <c>..</c> does not merely fail - it
/// addresses a <em>different</em> endpoint. That matters because two of the connection test's checks
/// exist precisely to prove the submitted operator id belongs to the submitted token: they conclude
/// <see cref="NayaxConnectionCheckFailure.OperatorIdMismatch"/> from a <c>404</c>, so a crafted id
/// that redirected the call to an endpoint the token <em>can</em> read would make "that operator is
/// yours" pass for an operator that is not. Rejecting anything but digits removes the whole class,
/// and <c>NayaxLynxClient</c> additionally escapes the value it builds a path from, so a legacy row
/// written before this rule existed cannot reach another endpoint either.</para>
///
/// <para>It holds no clock, no HTTP type and no configuration: every answer is a function of the
/// string handed in, which is what lets the Application boundary, the connection test and the
/// migration command all judge an operator id by one rule.</para>
/// </summary>
public static class NayaxOperatorId
{
    /// <summary>
    /// The most digits a value may carry: <c>long.MaxValue</c> is 19 digits, and the contract is an
    /// <c>int64</c>, so a longer run of digits is not an operator id however it is read.
    /// </summary>
    public const int MaxDigits = 19;

    /// <summary>
    /// The refusal a caller is shown. It names the field and the shape expected, and never any part
    /// of the submitted value: an operator id is not a secret, but echoing submitted input back into
    /// a message is how one ends up in a log.
    /// </summary>
    public const string InvalidMessage =
        "Enter the Nayax operator id as the number shown in Nayax Core under Operator › Company "
            + "Info › Operator Id. It is digits only.";

    /// <summary>
    /// Whether <paramref name="operatorId"/> is a usable Nayax operator id: digits only, at least
    /// one, at most <see cref="MaxDigits"/>, and greater than zero.
    ///
    /// <para>Written as "every character is an ASCII digit" rather than as a numeric parse alone,
    /// because a parse accepts a leading sign, surrounding whitespace, thousands separators and
    /// other-script digits depending on the format it is given - and any of those in a request path
    /// is the problem this rule exists to prevent. Zero is refused with them: no operator is
    /// operator 0, and a string of zeros is the one all-digit value that cannot identify one.</para>
    /// </summary>
    public static bool IsValid(string? operatorId)
    {
        if (string.IsNullOrEmpty(operatorId) || operatorId.Length > MaxDigits)
        {
            return false;
        }

        var allZero = true;

        foreach (var character in operatorId)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }

            allZero &= character == '0';
        }

        return !allZero;
    }
}
