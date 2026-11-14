using Inventory.Domain.ReceiptScanning;
using Xunit;

namespace InventoryApi.Tests.Domain.ReceiptScanning;

/// <summary>
/// The rule that decides what may be recorded about an unavailable scan (issue #1236): the status,
/// the provider's error type, the provider's message capped at 200 characters on one line, and the
/// request id - each only when it is known.
/// </summary>
public class ReceiptReaderFailureTests
{
    /// <summary>A refusal keeps the status and each stated value exactly as the provider stated it.</summary>
    [Fact]
    public void A_refusal_keeps_the_status_the_error_type_the_message_and_the_request_id()
    {
        var failure = ReceiptReaderFailure.Refused(
            400, "invalid_request_error", "Schema is too complex", "req_011CTM9fictional");

        Assert.Equal(ReceiptReaderFailureKind.Refused, failure.Kind);
        Assert.Equal(400, failure.StatusCode);
        Assert.Equal("invalid_request_error", failure.ErrorType);
        Assert.Equal("Schema is too complex", failure.ErrorMessage);
        Assert.Equal("req_011CTM9fictional", failure.RequestId);
    }

    /// <summary>
    /// A message longer than the cap is cut to its first 200 characters, and nothing of the rest
    /// survives anywhere.
    /// </summary>
    [Fact]
    public void A_long_message_is_cut_to_its_first_200_characters()
    {
        var message = new string('x', 199) + "END" + new string('y', 500);

        var failure = ReceiptReaderFailure.Refused(400, errorMessage: message);

        Assert.Equal(ReceiptReaderFailure.MaxTextLength, failure.ErrorMessage!.Length);
        Assert.Equal(message[..ReceiptReaderFailure.MaxTextLength], failure.ErrorMessage);
        Assert.DoesNotContain("y", failure.ErrorMessage, StringComparison.Ordinal);
    }

    /// <summary>
    /// Line breaks, carriage returns, tabs and other control characters each become one space, so a
    /// provider message cannot spread the scan's reason over several log lines.
    /// </summary>
    [Fact]
    public void Line_breaks_and_control_characters_become_spaces_on_one_line()
    {
        // Unicode LINE SEPARATOR by code point: it is a line break the source file itself could not
        // hold as a character.
        var stated = "first line\r\nsecond\tthird\u0000fourth" + (char)0x2028 + "fifth";

        var failure = ReceiptReaderFailure.Refused(400, errorMessage: stated);

        Assert.Equal("first line  second third fourth fifth", failure.ErrorMessage);
    }

    /// <summary>The ends are trimmed, and a value with nothing in it is recorded as nothing at all.</summary>
    [Theory]
    [InlineData("  padded  ", "padded")]
    [InlineData("\n\n", null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void A_blank_value_is_recorded_as_nothing(string? stated, string? expected)
    {
        var failure = ReceiptReaderFailure.Refused(429, errorType: stated, errorMessage: stated, requestId: stated);

        Assert.Equal(expected, failure.ErrorType);
        Assert.Equal(expected, failure.ErrorMessage);
        Assert.Equal(expected, failure.RequestId);
    }

    /// <summary>
    /// A long message whose 200th character is a space does not end on one: the cut is at most the
    /// cap, never more.
    /// </summary>
    [Fact]
    public void A_cut_message_never_ends_on_a_space()
    {
        var failure = ReceiptReaderFailure.Refused(
            400, errorMessage: new string('x', 198) + "\n\n" + new string('y', 50));

        Assert.Equal(new string('x', 198), failure.ErrorMessage);
    }

    /// <summary>
    /// A refusal that stated nothing still records its status: the status is the fact, and nothing is
    /// guessed from it.
    /// </summary>
    [Fact]
    public void A_refusal_that_stated_nothing_still_records_its_status()
    {
        var failure = ReceiptReaderFailure.Refused(529);

        Assert.Equal(529, failure.StatusCode);
        Assert.Null(failure.ErrorType);
        Assert.Null(failure.ErrorMessage);
        Assert.Null(failure.RequestId);
    }

    /// <summary>
    /// A failure with no answer to read records which one it was and <strong>no status</strong> -
    /// there was none, and a status nobody answered would be a reading of a failure that did not
    /// happen.
    /// </summary>
    [Theory]
    [InlineData(ReceiptReaderFailureKind.NetworkError)]
    [InlineData(ReceiptReaderFailureKind.UnreadableAnswer)]
    [InlineData(ReceiptReaderFailureKind.NoTextBlock)]
    public void A_local_failure_records_its_kind_and_no_status(ReceiptReaderFailureKind kind)
    {
        var failure = ReceiptReaderFailure.Local(kind);

        Assert.Equal(kind, failure.Kind);
        Assert.Null(failure.StatusCode);
        Assert.Null(failure.ErrorType);
        Assert.Null(failure.ErrorMessage);
        Assert.Null(failure.RequestId);
    }

    /// <summary>A refusal is never built as a local failure: it has a status, and it must carry it.</summary>
    [Fact]
    public void A_refusal_cannot_be_recorded_as_a_local_failure() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ReceiptReaderFailure.Local(ReceiptReaderFailureKind.Refused));

    /// <summary>
    /// A withheld message is recorded as withheld and never as a value, whatever was passed in
    /// (the owner's decision of 14 November 2026); everything else the refusal stated stands.
    /// </summary>
    [Fact]
    public void A_withheld_message_is_recorded_as_withheld_and_not_as_text()
    {
        var failure = ReceiptReaderFailure.Refused(
            400, "invalid_request_error", "quotes Cola 375mL", "req_1", messageWithheld: true);

        Assert.Null(failure.ErrorMessage);
        Assert.True(failure.MessageWithheld);
        Assert.Equal(400, failure.StatusCode);
        Assert.Equal("invalid_request_error", failure.ErrorType);
        Assert.Equal("req_1", failure.RequestId);
    }

    /// <summary>A message that was not withheld says so, as does every local failure.</summary>
    [Fact]
    public void A_message_is_not_withheld_unless_the_reader_says_so()
    {
        Assert.False(ReceiptReaderFailure.Refused(400, errorMessage: "Overloaded").MessageWithheld);
        Assert.False(ReceiptReaderFailure.Local(ReceiptReaderFailureKind.NetworkError).MessageWithheld);
    }
}
