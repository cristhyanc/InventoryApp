using System.Text;

namespace Inventory.Domain.ReceiptScanning;

/// <summary>
/// Which failure made the receipt reader unusable (issue #1236). It is a declared vocabulary rather
/// than free text, because it is the one thing the log says about a failure that no upstream value
/// could ever colour.
/// </summary>
public enum ReceiptReaderFailureKind
{
    /// <summary>
    /// The reader answered and refused. This is the only kind that carries an HTTP status, and the
    /// only one that can carry anything the provider said.
    /// </summary>
    Refused = 10,

    /// <summary>No answer arrived at all: the request failed in transport.</summary>
    NetworkError = 20,

    /// <summary>An answer arrived and was not the JSON it claims to be.</summary>
    UnreadableAnswer = 30,

    /// <summary>
    /// An answer arrived and was read, and it carried no text block this application can use - which
    /// includes an answer that is not an object at all.
    /// </summary>
    NoTextBlock = 40,
}

/// <summary>
/// Why one receipt scan ended as unavailable (issue #1236), in the only shape it may be recorded in.
///
/// <para><strong>It is what the owner may be told, and it is the whole of it.</strong> A scan sends a
/// receipt, so an upstream error body - which can quote the request - is never logged, stored or
/// echoed. What a failure may carry is the upstream HTTP status, the provider's own error
/// <c>type</c>, the provider's own error <c>message</c> capped at <see cref="MaxTextLength"/>
/// characters on one line - unless that message quoted the request, when it is withheld - the provider's request id, and which local failure it was when there was
/// no answer to read. <strong>No member of this type is for the file, a file name, a product or
/// supplier name, a line of the receipt, the prompt or the key</strong>: the provider's message is
/// the only free text any of them could arrive in, and the adapter withholds it when it is seen to
/// quote one (<c>ReceiptRequestEcho</c>, whose own summary records what that recognition cannot
/// catch).</para>
///
/// <para><strong>The cap is the type's own, not its caller's.</strong> Every upstream string is put
/// through <see cref="Clean"/> by the factory methods below, the constructor is private and the
/// record is sealed - so its copy constructor is private too and no <c>with</c> expression can put
/// an uncapped, multi-line value back in. The owner decided the cap on 13 November 2026 for the
/// provider's message; the error type and the request id travel the same way for the same reason,
/// because they come from the same answer.</para>
/// </summary>
public sealed record ReceiptReaderFailure
{
    /// <summary>How much of an upstream string is kept: the first 200 characters, on one line.</summary>
    public const int MaxTextLength = 200;

    private ReceiptReaderFailure(
        ReceiptReaderFailureKind kind,
        int? statusCode,
        string? errorType,
        string? errorMessage,
        string? requestId,
        bool messageWithheld)
    {
        Kind = kind;
        StatusCode = statusCode;
        ErrorType = errorType;
        ErrorMessage = errorMessage;
        RequestId = requestId;
        MessageWithheld = messageWithheld;
    }

    /// <summary>Which failure it was.</summary>
    public ReceiptReaderFailureKind Kind { get; }

    /// <summary>
    /// The upstream HTTP status, when the reader answered one; <see langword="null"/> when no answer
    /// was read, so a local failure records no status rather than a made-up one.
    /// </summary>
    public int? StatusCode { get; }

    /// <summary>
    /// The provider's own error <c>type</c> - <c>invalid_request_error</c>, <c>overloaded_error</c>
    /// and the like - when the error body stated one, and <see langword="null"/> when it did not.
    /// Nothing is inferred from the status.
    /// </summary>
    public string? ErrorType { get; }

    /// <summary>
    /// The provider's own error <c>message</c>, cleaned and capped, when the error body stated one.
    /// It is the one piece of free text a failure may carry.
    /// </summary>
    public string? ErrorMessage { get; }

    /// <summary>
    /// Whether the provider stated a message and it was not recorded because it quoted what the
    /// request carried (the owner's decision of 14 November 2026): a product or supplier name of the
    /// business, or a piece of the file. <see cref="ErrorMessage"/> is then <see langword="null"/>,
    /// and the log says the message was withheld rather than that none was stated.
    /// </summary>
    public bool MessageWithheld { get; }

    /// <summary>
    /// The provider's request id, when the response carried one, so a failure can be traced with the
    /// provider.
    /// </summary>
    public string? RequestId { get; }

    /// <summary>
    /// A refusal the reader answered: the status is known, and each of the other three is recorded
    /// only when the answer actually stated it.
    /// </summary>
    /// <param name="statusCode">The upstream HTTP status.</param>
    /// <param name="errorType">The error body's <c>error.type</c>, or <see langword="null"/>.</param>
    /// <param name="errorMessage">The error body's <c>error.message</c>, or <see langword="null"/>.</param>
    /// <param name="requestId">The response's request id, or <see langword="null"/>.</param>
    /// <param name="messageWithheld">
    /// <see langword="true"/> when the provider stated a message that quoted the request, so it is
    /// not recorded: <paramref name="errorMessage"/> is then ignored.
    /// </param>
    public static ReceiptReaderFailure Refused(
        int statusCode,
        string? errorType = null,
        string? errorMessage = null,
        string? requestId = null,
        bool messageWithheld = false) =>
        new(
            ReceiptReaderFailureKind.Refused,
            statusCode,
            Clean(errorType),
            messageWithheld ? null : Clean(errorMessage),
            Clean(requestId),
            messageWithheld);

    /// <summary>
    /// A failure with no answer to take a status from: which one it was, and nothing else at all.
    /// </summary>
    /// <param name="kind">
    /// The local failure. <see cref="ReceiptReaderFailureKind.Refused"/> is not one: a refusal has a
    /// status and is built by <see cref="Refused"/>.
    /// </param>
    public static ReceiptReaderFailure Local(ReceiptReaderFailureKind kind)
    {
        if (kind is ReceiptReaderFailureKind.Refused)
        {
            throw new ArgumentOutOfRangeException(
                nameof(kind), kind, "A refusal carries the status the reader answered.");
        }

        return new ReceiptReaderFailure(kind, statusCode: null, errorType: null, errorMessage: null, requestId: null, messageWithheld: false);
    }

    /// <summary>
    /// One upstream string as it may be recorded: every line break, control character and Unicode
    /// line separator becomes a space, the ends are trimmed, and what is left is cut to its first
    /// <see cref="MaxTextLength"/> characters. A value with nothing in it is
    /// <see langword="null"/> - an empty string in a log reads as a value that was stated.
    /// </summary>
    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var flattened = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            flattened.Append(IsBreak(character) ? ' ' : character);
        }

        var cleaned = flattened.ToString().Trim();

        return cleaned.Length > MaxTextLength
            ? cleaned[..MaxTextLength].TrimEnd()
            : cleaned;
    }

    /// <summary>
    /// Whether a character would put the rest of the value on another line, or is a control
    /// character a log sink has no use for.
    /// </summary>
    private static bool IsBreak(char character) =>
        char.IsControl(character) || character == LineSeparator || character == ParagraphSeparator;

    /// <summary>Unicode LINE SEPARATOR: a line break a control-character test does not catch.</summary>
    private const char LineSeparator = (char)0x2028;

    /// <summary>Unicode PARAGRAPH SEPARATOR, on the same terms.</summary>
    private const char ParagraphSeparator = (char)0x2029;
}
