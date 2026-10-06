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

    /// <summary>
    /// Walks the statement once, tracking the four contexts in which a character means something
    /// other than itself - line comment, block comment, string literal, quoted identifier - and
    /// produces all three answers from that single pass.
    ///
    /// A trailing semicolon with only whitespace or comments after it is one statement, not two:
    /// <see cref="ScanResult.SawStatementSeparator"/> is set only when real content follows the
    /// separator, because refusing the terminator people habitually type would be a usability tax
    /// that buys no safety.
    /// </summary>
    private static ScanResult Scan(string sql)
    {
        var normalized = new StringBuilder(sql.Length);
        string? firstKeyword = null;
        var contentAfterSeparator = false;
        var separatorSeen = false;

        var index = 0;
        while (index < sql.Length)
        {
            var current = sql[index];

            // Comments carry no shape and no content: they collapse to one space.
            if (current == '-' && index + 1 < sql.Length && sql[index + 1] == '-')
            {
                while (index < sql.Length && sql[index] != '\n')
                {
                    index++;
                }

                AppendSpace(normalized);
                continue;
            }

            if (current == '/' && index + 1 < sql.Length && sql[index + 1] == '*')
            {
                index += 2;
                while (index + 1 < sql.Length && !(sql[index] == '*' && sql[index + 1] == '/'))
                {
                    index++;
                }

                index = Math.Min(sql.Length, index + 2);
                AppendSpace(normalized);
                continue;
            }

            if (char.IsWhiteSpace(current))
            {
                index++;
                AppendSpace(normalized);
                continue;
            }

            if (current == ';')
            {
                index++;
                separatorSeen = true;
                continue;
            }

            if (separatorSeen)
            {
                contentAfterSeparator = true;
            }

            // A string literal is a value, so its shape is "?" - and its contents, semicolons and
            // keywords included, are not part of the statement's structure.
            if (current == '\'')
            {
                index = SkipQuoted(sql, index, '\'');
                normalized.Append('?');
                continue;
            }

            // Quoted identifiers are names, so they are kept (lower-cased) rather than masked, but
            // everything inside them is literal text and must not be read as structure.
            if (current is '"' or '`')
            {
                var closing = current;
                var start = index;
                index = SkipQuoted(sql, index, closing);
                AppendLowered(normalized, sql.AsSpan(start, index - start));
                continue;
            }

            if (current == '[')
            {
                var start = index;
                index++;
                while (index < sql.Length && sql[index] != ']')
                {
                    index++;
                }

                index = Math.Min(sql.Length, index + 1);
                AppendLowered(normalized, sql.AsSpan(start, index - start));
                continue;
            }

            if (char.IsAsciiDigit(current)
                || (current == '.' && index + 1 < sql.Length && char.IsAsciiDigit(sql[index + 1])))
            {
                while (index < sql.Length
                    && (char.IsAsciiLetterOrDigit(sql[index]) || sql[index] is '.' or '+' or '-'))
                {
                    // A sign only continues a number straight after an exponent marker.
                    if (sql[index] is '+' or '-' && !(sql[index - 1] is 'e' or 'E'))
                    {
                        break;
                    }

                    index++;
                }

                normalized.Append('?');
                continue;
            }

            if (char.IsLetter(current) || current == '_')
            {
                var start = index;
                while (index < sql.Length && (char.IsLetterOrDigit(sql[index]) || sql[index] is '_' or '$'))
                {
                    index++;
                }

                var word = sql.AsSpan(start, index - start);
                firstKeyword ??= word.ToString();
                AppendLowered(normalized, word);
                continue;
            }

            normalized.Append(current);
            index++;
        }

        return new ScanResult(
            firstKeyword,
            separatorSeen && contentAfterSeparator,
            normalized.ToString().Trim());
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
