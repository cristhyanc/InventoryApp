using Inventory.Application.ReceiptScanning;

namespace Inventory.Infrastructure.ReceiptScanning;

/// <summary>
/// Whether a provider's error message quotes what a receipt-scan request carried (issue #1236, the
/// owner's decision of 14 November 2026). A message that does is not logged; the status, the error
/// type and the request id still are.
///
/// <para><strong>Everything the request carries is looked at</strong>: the file (as base64), the
/// catalogue text with this business's product and supplier names, the system prompt, the output
/// schema and the API key. The provider refuses a malformed request before it reads the receipt, so
/// a refusal cannot quote what is printed on the receipt; it can only quote the request.</para>
///
/// <para><strong>Five rules, and what each is for.</strong> A product or supplier name of three
/// characters or more is looked for anywhere in the message; a name of one or two characters only as
/// a name of its own, between characters that are not letters or digits - so a supplier called
/// <c>7D</c> is caught in "Supplier '7D' is not a valid value" while a product called <c>co</c> does
/// not withhold "Schema is too complex" (<see cref="MinNameLength"/>). Every comparison ignores case.
/// A run of <see cref="MinFileFragmentLength"/> base64 characters that appears in the encoded file is
/// a piece of the file. A run of <see cref="MinKeyFragmentLength"/> characters of the key is the key:
/// a credential is random, so no message contains eight of its characters by accident. Any
/// <see cref="MinTextFragmentLength"/> characters of the prompt, schema or catalogue text is a quote
/// of them, and so is a single <em>distinctive</em> word of those texts - one of
/// <see cref="MinDistinctiveTokenLength"/> characters or more that carries a digit, an inner capital
/// or an inner <c>-</c>, <c>_</c> or <c>.</c> (<c>unitsPerPack</c>, <c>yyyy-MM-dd</c>,
/// <c>vending-machine</c>) - because nothing but this application's own text produces one.</para>
///
/// <para><strong>What is deliberately not caught, and why it is not a leak.</strong> An ordinary
/// English word of the fixed prompt or schema ("printed", "delivery", "single") is indistinguishable
/// from the provider's own wording, so it is not looked for; the fixed prompt and schema are this
/// application's text, identical for every business, and hold nothing about the business or the
/// receipt. <strong>Nothing about the business</strong> - a catalogue name of any length, any part of
/// the file, the key - is left to a threshold of that kind. A quote shorter than a rule's own minimum
/// is the residual exposure this heuristic keeps, recorded in docs/architecture.md § Receipt scanning
/// and README § receipt scanning; a guarantee rather than a heuristic would mean logging no provider
/// message at all, which is the owner's decision of 13 November 2026 to make, not this type's.</para>
///
/// <para>The check is deliberately wide: a message that merely contains a product or supplier name,
/// in any case, is withheld. Losing one diagnostic message is the cheaper mistake.</para>
/// </summary>
internal static class ReceiptRequestEcho
{
    /// <summary>
    /// The shortest catalogue name looked for anywhere in a message. A shorter one is looked for as a
    /// name of its own instead (see <see cref="QuotesName"/>), because "co" inside "complex" says
    /// nothing about the business while a supplier genuinely called "7D" does.
    /// </summary>
    internal const int MinNameLength = 3;

    /// <summary>
    /// The shortest run of base64 characters that counts as a piece of the file. Sixteen characters
    /// are twelve bytes of the file: long enough that ordinary words in a message never match one by
    /// chance, short enough that a quoted fragment is caught.
    /// </summary>
    internal const int MinFileFragmentLength = 16;

    /// <summary>
    /// The shortest run of the API key that counts as a quote of it. A key is random, so eight of its
    /// characters appear in no ordinary message by chance - and a message quoting even part of a
    /// credential must never be logged.
    /// </summary>
    internal const int MinKeyFragmentLength = 8;

    /// <summary>
    /// The shortest run of the request's own prose (prompt, schema, catalogue) that counts as a quote
    /// of it, compared with case and runs of whitespace ignored. Long enough that the vendor's
    /// ordinary wording ("Schema is too complex") never matches by chance, short enough that a quote
    /// of a sentence is caught.
    /// </summary>
    internal const int MinTextFragmentLength = 24;

    /// <summary>
    /// The shortest word of the request's own text that is distinctive enough to be a quote of it on
    /// its own (see <see cref="IsDistinctive"/>): shorter than a sentence, so it closes the gap the
    /// prose rule leaves for a short verbatim quote such as <c>unitsPerPack</c>.
    /// </summary>
    internal const int MinDistinctiveTokenLength = 6;

    /// <summary>
    /// <see langword="true"/> when <paramref name="message"/> quotes anything
    /// <paramref name="request"/>, <paramref name="requestTexts"/> (the prompt, the schema and the
    /// catalogue text) or <paramref name="apiKey"/> carried, by any of the five rules described on
    /// this type.
    /// </summary>
    internal static bool Quotes(
        string message,
        ReceiptReadRequest request,
        IEnumerable<string?> requestTexts,
        string? apiKey)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(requestTexts);

        var texts = requestTexts as IReadOnlyCollection<string?> ?? [.. requestTexts];
        if (QuotesKey(message, apiKey) || QuotesText(message, texts) || QuotesWord(message, texts))
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
        return Runs(message, IsBase64, MinFileFragmentLength)
            .Any(run => encodedFile.Contains(run, StringComparison.Ordinal));
    }

    /// <summary>
    /// Whether the message holds the key or a run of <see cref="MinKeyFragmentLength"/> of its
    /// characters. A key shorter than that run - only a test fixture's would be - is looked for
    /// whole, so no key is unchecked merely for being short.
    /// </summary>
    private static bool QuotesKey(string message, string? apiKey)
    {
        var key = apiKey?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        return key.Length < MinKeyFragmentLength
            ? message.Contains(key, StringComparison.OrdinalIgnoreCase)
            : Runs(message, IsKeyCharacter, MinKeyFragmentLength)
                .Any(run => key.Contains(run, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Whether the message holds a distinctive word of the request's own text - the short verbatim
    /// quote (<c>unitsPerPack</c>, <c>yyyy-MM-dd</c>) that the prose rule's 24 characters let through.
    /// </summary>
    private static bool QuotesWord(string message, IReadOnlyCollection<string?> requestTexts)
    {
        foreach (var text in requestTexts)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            foreach (var word in Words(text))
            {
                if (IsDistinctive(word) && message.Contains(word, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Every word of a text: each run of letters, digits and inner <c>-</c>, <c>_</c> or <c>.</c>,
    /// with that punctuation trimmed off the ends, so <c>"unitsPerPack",</c> in the schema's JSON and
    /// <c>yyyy-MM-dd.</c> in the prompt are read as the words they are.
    /// </summary>
    private static IEnumerable<string> Words(string text)
    {
        var start = 0;
        while (start < text.Length)
        {
            while (start < text.Length && !IsWordCharacter(text[start]))
            {
                start++;
            }

            var end = start;
            while (end < text.Length && IsWordCharacter(text[end]))
            {
                end++;
            }

            if (end > start)
            {
                yield return text[start..end].Trim('-', '_', '.');
            }

            start = end;
        }
    }

    /// <summary>
    /// Whether a word of the request's own text is one the provider could not have written itself: at
    /// least <see cref="MinDistinctiveTokenLength"/> characters long and carrying a digit, a capital
    /// after its first character, or inner punctuation. An ordinary English word is not distinctive,
    /// deliberately - see this type's summary.
    /// </summary>
    private static bool IsDistinctive(string word)
    {
        if (word.Length < MinDistinctiveTokenLength)
        {
            return false;
        }

        for (var index = 0; index < word.Length; index++)
        {
            var character = word[index];
            if (char.IsAsciiDigit(character)
                || (index > 0 && char.IsUpper(character))
                || (index > 0 && index < word.Length - 1 && character is '-' or '_' or '.'))
            {
                return true;
            }
        }

        return false;
    }

    private static bool QuotesText(string message, IReadOnlyCollection<string?> requestTexts)
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

    /// <summary>
    /// Whether the message quotes one catalogue name, ignoring case. A name of
    /// <see cref="MinNameLength"/> characters or more is looked for anywhere in the message; a
    /// shorter one - a real two-character supplier such as "7D" - only where it stands as a name of
    /// its own, with no letter or digit beside it, so it is caught where it is quoted without
    /// withholding every message whose own words happen to contain those letters.
    /// </summary>
    private static bool QuotesName(string message, string? name)
    {
        if (name?.Trim() is not { Length: > 0 } trimmed)
        {
            return false;
        }

        if (trimmed.Length >= MinNameLength)
        {
            return message.Contains(trimmed, StringComparison.OrdinalIgnoreCase);
        }

        for (var index = message.IndexOf(trimmed, StringComparison.OrdinalIgnoreCase);
            index >= 0;
            index = message.IndexOf(trimmed, index + 1, StringComparison.OrdinalIgnoreCase))
        {
            var end = index + trimmed.Length;
            if ((index == 0 || !char.IsLetterOrDigit(message[index - 1]))
                && (end == message.Length || !char.IsLetterOrDigit(message[end])))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Every maximal run of <paramref name="belongs"/> characters in <paramref name="message"/> that
    /// is at least <paramref name="length"/> long, cut into windows of exactly that length so a
    /// fragment framed by more of the same characters (a quote that starts mid-block) is still found.
    /// </summary>
    private static IEnumerable<string> Runs(string message, Func<char, bool> belongs, int length)
    {
        var start = 0;
        while (start < message.Length)
        {
            while (start < message.Length && !belongs(message[start]))
            {
                start++;
            }

            var end = start;
            while (end < message.Length && belongs(message[end]))
            {
                end++;
            }

            for (var offset = start; offset + length <= end; offset++)
            {
                yield return message.Substring(offset, length);
            }

            start = end;
        }
    }

    private static bool IsBase64(char character) =>
        character is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '+' or '/';

    /// <summary>The characters an API key is made of, so a run of them can be compared with one.</summary>
    private static bool IsKeyCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is '-' or '_';

    /// <summary>The characters a word of the request's own text is made of (see <see cref="Words"/>).</summary>
    private static bool IsWordCharacter(char character) =>
        char.IsLetterOrDigit(character) || character is '-' or '_' or '.';
}
