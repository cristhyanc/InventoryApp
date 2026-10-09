using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Inventory.Application.PlatformDiagnostics;

/// <summary>
/// The deterministic, transport-free facts about a submitted diagnostics statement (issue #336):
/// how large it is, whether it is a single read, and what its shape fingerprints to.
///
/// <para><strong>This is not the security boundary.</strong> It is a shape check, and it is
/// deliberately <em>not</em> trusted to decide what the statement may touch. Table and column
/// access, write and DDL refusal, <c>PRAGMA</c>, <c>ATTACH</c> and function restriction are all
/// enforced inside SQLite by the Infrastructure adapter - a read-only connection, <c>query_only</c>,
/// a zero attached-database limit and an authorizer callback consulted during preparation for
/// every table, column and operation the statement actually reaches, through joins, aliases,
/// subqueries and expressions alike. What this type adds is the part a callback cannot see: that
/// one statement was submitted rather than several, and that it was submitted as a read rather
/// than something whose refusal we would rather not depend on a single mechanism for.</para>
///
/// <para>The scanner is a character walk, not a pattern match, because the things that defeat a
/// pattern match here are ordinary SQL: a semicolon inside a string literal, a keyword inside a
/// quoted identifier, and a statement hidden behind a comment. Each of those is a state in the
/// walk below.</para>
/// </summary>
public static class DiagnosticsSqlShape
{
    /// <summary>The two statement starts a diagnostics read may use.</summary>
    private static readonly string[] PermittedLeadingKeywords = ["SELECT", "WITH"];

    public static int Utf8ByteCount(string? sql) =>
        sql is null ? 0 : Encoding.UTF8.GetByteCount(sql);

    /// <summary>
    /// Checks the statement's size and shape in the order a refusal should happen: an oversized
    /// statement is rejected before anything else looks at it, so it never reaches preparation.
    /// </summary>
    public static bool IsPermittedShape(
        string? sql,
        out DiagnosticsQueryDenialReason reason,
        out string message)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            reason = DiagnosticsQueryDenialReason.SqlMissing;
            message = "Submit one read-only SQL statement.";
            return false;
        }

        var byteCount = Utf8ByteCount(sql);
        if (byteCount > PlatformDiagnosticsQueryLimits.MaxSqlBytes)
        {
            reason = DiagnosticsQueryDenialReason.SqlTooLarge;
            message = string.Format(
                CultureInfo.InvariantCulture,
                "The submitted statement is {0} UTF-8 bytes; the maximum is {1}.",
                byteCount,
                PlatformDiagnosticsQueryLimits.MaxSqlBytes);
            return false;
        }

        var tokens = Scan(sql);

        if (tokens.SawStatementSeparator)
        {
            reason = DiagnosticsQueryDenialReason.MultipleStatements;
            message = "Submit exactly one statement; multi-statement input is refused.";
            return false;
        }

        if (tokens.FirstKeyword is null
            || !PermittedLeadingKeywords.Contains(tokens.FirstKeyword, StringComparer.OrdinalIgnoreCase))
        {
            reason = DiagnosticsQueryDenialReason.NotAReadOnlyStatement;
            message = "Only a single SELECT (optionally led by WITH) is permitted.";
            return false;
        }

        reason = DiagnosticsQueryDenialReason.None;
        message = string.Empty;
        return true;
    }

    /// <summary>
    /// The query <em>shape</em>: the statement with every literal replaced by <c>?</c>, comments
    /// removed, whitespace collapsed and words lower-cased. Two investigations that differ only in
    /// the ids they look for share one shape, which is what makes the audit trail readable, and the
    /// shape carries no value a caller typed - which is what keeps a copied-in identifier out of
    /// the log.
    /// </summary>
    public static string NormalizeForFingerprint(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return string.Empty;
        }

        return Scan(sql).Normalized;
    }

    /// <summary>
    /// The SHA-256 of the normalised shape, as upper-case hex. The raw statement is never logged,
    /// never returned and never stored; this is the only thing an audit event says about what was
    /// asked, and it is stable across runs so two events can be compared.
    /// </summary>
    public static string Fingerprint(string? sql)
    {
        var normalized = NormalizeForFingerprint(sql);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash);
    }

    /// <summary>What one walk of the statement found.</summary>
    private sealed record ScanResult(string? FirstKeyword, bool SawStatementSeparator, string Normalized);

    /// <summary>Walks the statement once; see <see cref="Scanner"/> for the walk itself.</summary>
    private static ScanResult Scan(string sql) => Scanner.Run(sql);

    /// <summary>
    /// Walks a statement once, tracking the four contexts in which a character means something
    /// other than itself - line comment, block comment, string literal, quoted identifier - and
    /// produces all three <see cref="ScanResult"/> answers from that single pass.
    ///
    /// Each context is its own method so the walk itself (<see cref="Walk"/>) stays a flat
    /// dispatch: every character is offered to each recogniser in turn, and the first one that
    /// claims it advances <see cref="_index"/> and returns <c>true</c>.
    ///
    /// A trailing semicolon with only whitespace or comments after it is one statement, not two:
    /// <see cref="_contentAfterSeparator"/> is set only when real content follows the separator,
    /// because refusing the terminator people habitually type would be a usability tax that buys
    /// no safety.
    /// </summary>
    private sealed class Scanner
    {
        private readonly string _sql;
        private readonly StringBuilder _normalized;
        private string? _firstKeyword;
        private bool _separatorSeen;
        private bool _contentAfterSeparator;
        private int _index;

        private Scanner(string sql)
        {
            _sql = sql;
            _normalized = new StringBuilder(sql.Length);
        }

        public static ScanResult Run(string sql)
        {
            var scanner = new Scanner(sql);
            scanner.Walk();

            return new ScanResult(
                scanner._firstKeyword,
                scanner._separatorSeen && scanner._contentAfterSeparator,
                scanner._normalized.ToString().Trim());
        }

        private void Walk()
        {
            while (_index < _sql.Length)
            {
                if (TryConsumeLineComment())
                {
                    continue;
                }

                if (TryConsumeBlockComment())
                {
                    continue;
                }

                if (TryConsumeWhitespace())
                {
                    continue;
                }

                if (TryConsumeSeparator())
                {
                    continue;
                }

                MarkContentIfAfterSeparator();

                if (TryConsumeStringLiteral())
                {
                    continue;
                }

                if (TryConsumeQuotedIdentifier())
                {
                    continue;
                }

                if (TryConsumeBracketIdentifier())
                {
                    continue;
                }

                if (TryConsumeNumber())
                {
                    continue;
                }

                if (TryConsumeWord())
                {
                    continue;
                }

                ConsumeOtherCharacter();
            }
        }

        // Comments carry no shape and no content: they collapse to one space.
        private bool TryConsumeLineComment()
        {
            if (_sql[_index] != '-' || _index + 1 >= _sql.Length || _sql[_index + 1] != '-')
            {
                return false;
            }

            while (_index < _sql.Length && _sql[_index] != '\n')
            {
                _index++;
            }

            AppendSpace(_normalized);
            return true;
        }

        private bool TryConsumeBlockComment()
        {
            if (_sql[_index] != '/' || _index + 1 >= _sql.Length || _sql[_index + 1] != '*')
            {
                return false;
            }

            _index += 2;
            while (_index + 1 < _sql.Length && !(_sql[_index] == '*' && _sql[_index + 1] == '/'))
            {
                _index++;
            }

            _index = Math.Min(_sql.Length, _index + 2);
            AppendSpace(_normalized);
            return true;
        }

        private bool TryConsumeWhitespace()
        {
            if (!char.IsWhiteSpace(_sql[_index]))
            {
                return false;
            }

            _index++;
            AppendSpace(_normalized);
            return true;
        }

        private bool TryConsumeSeparator()
        {
            if (_sql[_index] != ';')
            {
                return false;
            }

            _index++;
            _separatorSeen = true;
            return true;
        }

        private void MarkContentIfAfterSeparator()
        {
            if (_separatorSeen)
            {
                _contentAfterSeparator = true;
            }
        }

        // A string literal is a value, so its shape is "?" - and its contents, semicolons and
        // keywords included, are not part of the statement's structure.
        private bool TryConsumeStringLiteral()
        {
            if (_sql[_index] != '\'')
            {
                return false;
            }

            _index = SkipQuoted(_sql, _index, '\'');
            _normalized.Append('?');
            return true;
        }

        // Quoted identifiers are names, so they are kept (lower-cased) rather than masked, but
        // everything inside them is literal text and must not be read as structure.
        private bool TryConsumeQuotedIdentifier()
        {
            var current = _sql[_index];
            if (current is not ('"' or '`'))
            {
                return false;
            }

            var start = _index;
            _index = SkipQuoted(_sql, _index, current);
            AppendLowered(_normalized, _sql.AsSpan(start, _index - start));
            return true;
        }

        private bool TryConsumeBracketIdentifier()
        {
            if (_sql[_index] != '[')
            {
                return false;
            }

            var start = _index;
            _index++;
            while (_index < _sql.Length && _sql[_index] != ']')
            {
                _index++;
            }

            _index = Math.Min(_sql.Length, _index + 1);
            AppendLowered(_normalized, _sql.AsSpan(start, _index - start));
            return true;
        }

        private bool TryConsumeNumber()
        {
            var current = _sql[_index];
            var isNumberStart = char.IsAsciiDigit(current)
                || (current == '.' && _index + 1 < _sql.Length && char.IsAsciiDigit(_sql[_index + 1]));
            if (!isNumberStart)
            {
                return false;
            }

            while (_index < _sql.Length
                && (char.IsAsciiLetterOrDigit(_sql[_index]) || _sql[_index] is '.' or '+' or '-'))
            {
                // A sign only continues a number straight after an exponent marker.
                if (_sql[_index] is '+' or '-' && !(_sql[_index - 1] is 'e' or 'E'))
                {
                    break;
                }

                _index++;
            }

            _normalized.Append('?');
            return true;
        }

        private bool TryConsumeWord()
        {
            var current = _sql[_index];
            if (!char.IsLetter(current) && current != '_')
            {
                return false;
            }

            var start = _index;
            while (_index < _sql.Length && (char.IsLetterOrDigit(_sql[_index]) || _sql[_index] is '_' or '$'))
            {
                _index++;
            }

            var word = _sql.AsSpan(start, _index - start);
            _firstKeyword ??= word.ToString();
            AppendLowered(_normalized, word);
            return true;
        }

        private void ConsumeOtherCharacter()
        {
            _normalized.Append(_sql[_index]);
            _index++;
        }
    }

    /// <summary>
    /// Skips a quoted run, honouring the doubled-delimiter escape SQLite uses for all three quote
    /// characters, and returns the index just past the closing delimiter.
    /// </summary>
    private static int SkipQuoted(string sql, int openingIndex, char delimiter)
    {
        var index = openingIndex + 1;

        while (index < sql.Length)
        {
            if (sql[index] != delimiter)
            {
                index++;
                continue;
            }

            if (index + 1 < sql.Length && sql[index + 1] == delimiter)
            {
                index += 2;
                continue;
            }

            return index + 1;
        }

        return index;
    }

    private static void AppendSpace(StringBuilder builder)
    {
        if (builder.Length > 0 && builder[^1] != ' ')
        {
            builder.Append(' ');
        }
    }

    /// <summary>
    /// Lower-casing is the normalisation here rather than the analyzer-preferred upper-casing
    /// because the fingerprint is hashed, never compared or displayed as a string, and a lower-case
    /// shape is what a human reads in a reviewed audit sample.
    /// </summary>
    private static void AppendLowered(StringBuilder builder, ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            builder.Append(char.ToLowerInvariant(character));
        }
    }
}
