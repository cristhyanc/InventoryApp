using Inventory.Application.ReceiptScanning;

namespace Inventory.Infrastructure.ReceiptScanning;

/// <summary>
/// Whether a provider's error message quotes what a receipt-scan request carried (issue #1236, the
/// owner's decision of 14 November 2026). A message that does is not logged; the status, the error
/// type and the request id still are.
///
/// <para><strong>Everything the request carries is checked</strong>: the file (as base64), the
/// catalogue text with this business's product and supplier names, the system prompt, the output
/// schema and the API key. The provider refuses a malformed request before it reads the receipt, so
/// a refusal cannot quote what is printed on the receipt; it can only quote the request.</para>
///
/// <para>The check is deliberately wide: a message that merely contains a product or supplier name,
/// in any case, is withheld. Losing one diagnostic message is the cheaper mistake.</para>
/// </summary>
internal static class ReceiptRequestEcho
{
    /// <summary>
    /// The shortest catalogue name that is looked for. A one- or two-letter name would match words
    /// in almost every message and withhold them all, and says nothing about the business on its own.
    /// </summary>
    internal const int MinNameLength = 3;

    /// <summary>
    /// The shortest run of base64 characters that counts as a piece of the file. Sixteen characters
    /// are twelve bytes of the file: long enough that ordinary words in a message never match one by
    /// chance, short enough that a quoted fragment is caught.
    /// </summary>
    internal const int MinFileFragmentLength = 16;

    /// <summary>
    /// The shortest run of the request's own text (prompt, schema, catalogue, key) that counts as a
    /// quote of it, compared with case and runs of whitespace ignored. Long enough that the vendor's
    /// ordinary wording ("Schema is too complex") never matches by chance, short enough that a quote
    /// of a sentence is caught.
    /// </summary>
    internal const int MinTextFragmentLength = 24;

    /// <summary>
    /// <see langword="true"/> when <paramref name="message"/> contains a product or supplier name
    /// of <paramref name="request"/> (ignoring case), or a run of at least
    /// <see cref="MinFileFragmentLength"/> base64 characters that appears in the encoded file, or
    /// any <see cref="MinTextFragmentLength"/> characters of <paramref name="requestTexts"/> (the
    /// prompt, the schema, the catalogue text and the key), or any of those texts whole.
    /// </summary>
    internal static bool Quotes(string message, ReceiptReadRequest request, IEnumerable<string?> requestTexts)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(requestTexts);

        if (QuotesText(message, requestTexts))
        {
            return true;
        }

        var names = request.Products.Select(product => product.Name)
            .Concat(request.Suppliers.Select(supplier => supplier.Name));
        if (names.Any(name => QuotesName(message, name)))
        {
            return true;
        }

        var encodedFile = Convert.ToBase64String(request.File.Content);
        return Base64Runs(message).Any(run => encodedFile.Contains(run, StringComparison.Ordinal));
    }

    private static bool QuotesText(string message, IEnumerable<string?> requestTexts)
    {
        var quoted = Normalise(message);
        foreach (var text in requestTexts)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var source = Normalise(text);
            if (quoted.Contains(source, StringComparison.Ordinal))
            {
                return true;
            }

            for (var offset = 0; offset + MinTextFragmentLength <= quoted.Length; offset++)
            {
                if (source.Contains(quoted.AsSpan(offset, MinTextFragmentLength), StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Lower case, with every run of whitespace, control characters and JSON escapes of them
    /// (<c>\n</c>, <c>\t</c>, <c>\r</c>) made one space - so a quote re-wrapped or re-escaped by
    /// the vendor still compares equal to the text it came from.
    /// </summary>
    private static string Normalise(string value)
    {
        var unescaped = value.Replace("\\n", " ", StringComparison.Ordinal)
            .Replace("\\r", " ", StringComparison.Ordinal)
            .Replace("\\t", " ", StringComparison.Ordinal)
            .Replace("\\\"", "\"", StringComparison.Ordinal);
        var normalised = new System.Text.StringBuilder(unescaped.Length);
        var lastWasSpace = false;
        foreach (var character in unescaped)
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                if (!lastWasSpace)
                {
                    normalised.Append(' ');
                }

                lastWasSpace = true;
                continue;
            }

            normalised.Append(char.ToLowerInvariant(character));
            lastWasSpace = false;
        }

        return normalised.ToString().Trim();
    }

    private static bool QuotesName(string message, string? name)
    {
        var trimmed = name?.Trim();
        return trimmed is { Length: >= MinNameLength }
            && message.Contains(trimmed, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Every maximal run of base64 alphabet characters in <paramref name="message"/> that is at
    /// least <see cref="MinFileFragmentLength"/> long, cut into windows of exactly that length so a
    /// fragment framed by other base64 characters (a quote that starts mid-block) is still found.
    /// </summary>
    private static IEnumerable<string> Base64Runs(string message)
    {
        var start = 0;
        while (start < message.Length)
        {
            while (start < message.Length && !IsBase64(message[start]))
            {
                start++;
            }

            var end = start;
            while (end < message.Length && IsBase64(message[end]))
            {
                end++;
            }

            for (var offset = start; offset + MinFileFragmentLength <= end; offset++)
            {
                yield return message.Substring(offset, MinFileFragmentLength);
            }

            start = end;
        }
    }

    private static bool IsBase64(char character) =>
        character is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '+' or '/';
}
