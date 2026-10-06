using System.Text;
using Inventory.Application.PlatformDiagnostics;
using Xunit;

namespace InventoryApi.Tests.Application.PlatformDiagnostics;

/// <summary>
/// The shape check in front of the diagnostics executor (issue #336).
///
/// It is not the access boundary - SQLite's authorizer is, and
/// <c>SqliteDiagnosticsQueryExecutorTests</c> proves that - so these tests are about the two things
/// a callback inside SQLite cannot answer: that one statement was submitted, and that it was
/// submitted as a read. The interesting cases are the ones that defeat naive text matching: a
/// semicolon inside a string, a keyword inside a quoted identifier, and a statement hidden behind a
/// comment.
/// </summary>
public class DiagnosticsSqlShapeTests
{
    [Theory]
    [InlineData("SELECT Id FROM Products")]
    [InlineData("select id from products where businessid = 1")]
    [InlineData("  SELECT Id FROM Products ; ")]
    [InlineData("SELECT Id FROM Products -- a trailing comment\n")]
    [InlineData("/* leading */ SELECT Id FROM Products")]
    [InlineData("WITH owned AS (SELECT Id FROM Products) SELECT Id FROM owned")]
    [InlineData("SELECT Id FROM Products WHERE Id = 1 /* ; DROP TABLE Products */")]
    [InlineData("SELECT Id FROM Products WHERE Id = 1 -- ; DELETE FROM Products")]
    public void A_single_read_statement_is_permitted(string sql)
    {
        var permitted = DiagnosticsSqlShape.IsPermittedShape(sql, out var reason, out _);

        Assert.True(permitted);
        Assert.Equal(DiagnosticsQueryDenialReason.None, reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Nothing_submitted_is_refused_as_missing(string? sql)
    {
        Assert.False(DiagnosticsSqlShape.IsPermittedShape(sql, out var reason, out _));
        Assert.Equal(DiagnosticsQueryDenialReason.SqlMissing, reason);
    }

    [Theory]
    [InlineData("SELECT Id FROM Products; SELECT Id FROM Categories")]
    [InlineData("SELECT Id FROM Products;DELETE FROM Products")]
    [InlineData("SELECT Id FROM Products; -- second\nSELECT 1")]
    public void More_than_one_statement_is_refused(string sql)
    {
        Assert.False(DiagnosticsSqlShape.IsPermittedShape(sql, out var reason, out _));
        Assert.Equal(DiagnosticsQueryDenialReason.MultipleStatements, reason);
    }

    [Theory]
    [InlineData("DELETE FROM Products")]
    [InlineData("UPDATE Products SET BusinessId = 2")]
    [InlineData("INSERT INTO Categories (Id, BusinessId) VALUES (1, 1)")]
    [InlineData("DROP TABLE Products")]
    [InlineData("CREATE TABLE x (y INTEGER)")]
    [InlineData("ALTER TABLE Products ADD COLUMN z INTEGER")]
    [InlineData("PRAGMA table_info('Products')")]
    [InlineData("ATTACH DATABASE 'other.db' AS other")]
    [InlineData("VACUUM")]
    [InlineData("EXPLAIN SELECT Id FROM Products")]
    [InlineData("REPLACE INTO Products (Id) VALUES (1)")]
    public void Anything_that_is_not_a_leading_SELECT_or_WITH_is_refused(string sql)
    {
        Assert.False(DiagnosticsSqlShape.IsPermittedShape(sql, out var reason, out _));
        Assert.Equal(DiagnosticsQueryDenialReason.NotAReadOnlyStatement, reason);
    }

    /// <summary>
    /// A semicolon or a keyword inside a literal or a quoted identifier is data and a name, not
    /// structure. A check that looked for the characters rather than tracking the quoting state
    /// would refuse both of these.
    /// </summary>
    [Theory]
    [InlineData("SELECT Id FROM Products WHERE Id = 1 AND 'a;b' = 'a;b'")]
    [InlineData("SELECT Id FROM \"Products\" WHERE 'drop table' <> ''")]
    [InlineData("SELECT Id FROM [Products]")]
    public void Quoting_is_tracked_rather_than_pattern_matched(string sql)
    {
        Assert.True(DiagnosticsSqlShape.IsPermittedShape(sql, out _, out _));
    }

    [Fact]
    public void Sql_over_sixteen_kibibytes_is_refused_by_size_before_anything_else()
    {
        var oversized = "SELECT Id FROM Products WHERE Id = 1 AND '"
            + new string('x', PlatformDiagnosticsQueryLimits.MaxSqlBytes)
            + "' = ''";

        Assert.False(DiagnosticsSqlShape.IsPermittedShape(oversized, out var reason, out var message));
        Assert.Equal(DiagnosticsQueryDenialReason.SqlTooLarge, reason);
        Assert.Contains("16384", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The cap is UTF-8 bytes, not characters: a multi-byte statement that is comfortably under the
    /// limit by character count can still be over it by the measure that matters.
    /// </summary>
    [Fact]
    public void The_size_cap_counts_utf8_bytes_not_characters()
    {
        var multiByte = new string('é', (PlatformDiagnosticsQueryLimits.MaxSqlBytes / 2) + 1);
        var sql = $"SELECT Id FROM Products WHERE '{multiByte}' = ''";

        Assert.True(sql.Length < PlatformDiagnosticsQueryLimits.MaxSqlBytes);
        Assert.True(Encoding.UTF8.GetByteCount(sql) > PlatformDiagnosticsQueryLimits.MaxSqlBytes);
        Assert.False(DiagnosticsSqlShape.IsPermittedShape(sql, out var reason, out _));
        Assert.Equal(DiagnosticsQueryDenialReason.SqlTooLarge, reason);
    }

    [Fact]
    public void A_statement_at_exactly_the_size_cap_is_still_permitted()
    {
        const string prefix = "SELECT Id FROM Products WHERE Id = ";
        var sql = prefix + new string('1', PlatformDiagnosticsQueryLimits.MaxSqlBytes - prefix.Length);

        Assert.Equal(PlatformDiagnosticsQueryLimits.MaxSqlBytes, Encoding.UTF8.GetByteCount(sql));
        Assert.True(DiagnosticsSqlShape.IsPermittedShape(sql, out _, out _));
    }

    /// <summary>
    /// The fingerprint identifies the query's shape, so two investigations that differ only in the
    /// identifiers they look for must fingerprint identically - that is what makes the audit trail
    /// aggregatable - and a different shape must fingerprint differently.
    /// </summary>
    [Fact]
    public void The_fingerprint_is_the_query_shape_not_the_values()
    {
        var first = DiagnosticsSqlShape.Fingerprint("SELECT Id FROM Products WHERE BusinessId = 1");
        var second = DiagnosticsSqlShape.Fingerprint("select  id\nfrom products\nwhere businessid = 99812");
        var different = DiagnosticsSqlShape.Fingerprint("SELECT Id FROM Categories WHERE BusinessId = 1");

        Assert.Equal(first, second);
        Assert.NotEqual(first, different);
    }

    [Fact]
    public void The_fingerprint_is_a_sha256_hex_digest_and_contains_no_part_of_the_statement()
    {
        const string sql = "SELECT Id FROM Products WHERE BusinessId = 4 AND Sku = 'SECRET-SKU'";

        var fingerprint = DiagnosticsSqlShape.Fingerprint(sql);

        Assert.Equal(64, fingerprint.Length);
        Assert.All(fingerprint, character => Assert.Contains(character, "0123456789ABCDEF"));
        Assert.DoesNotContain("SECRET", fingerprint, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Products", fingerprint, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The normalised shape is what the fingerprint is taken over, and it is the thing a human
    /// might be shown in a reviewed audit sample, so it must carry no literal a caller typed.
    /// </summary>
    [Fact]
    public void Normalisation_masks_every_literal_and_collapses_comments_and_whitespace()
    {
        var normalized = DiagnosticsSqlShape.NormalizeForFingerprint(
            "SELECT   Id /* why */ FROM Products\n WHERE BusinessId = 17 AND Sku = 'ABC-123'");

        Assert.Equal("select id from products where businessid = ? and sku = ?", normalized);
    }

    [Fact]
    public void A_statement_that_is_rejected_still_fingerprints_so_the_attempt_can_be_audited()
    {
        var fingerprint = DiagnosticsSqlShape.Fingerprint("DROP TABLE Products");

        Assert.Equal(64, fingerprint.Length);
        Assert.NotEqual(DiagnosticsSqlShape.Fingerprint(string.Empty), fingerprint);
    }
}
