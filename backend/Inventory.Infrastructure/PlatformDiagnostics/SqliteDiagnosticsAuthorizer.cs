using Inventory.Application.PlatformDiagnostics;
using SQLitePCL;

namespace Inventory.Infrastructure.PlatformDiagnostics;

/// <summary>
/// The real authorization boundary for a platform-admin diagnostics query (issue #336): SQLite's
/// own authorizer callback, consulted by <c>sqlite3_prepare</c> for every table, column, function
/// and operation the statement actually reaches.
///
/// <para>This is why the acceptance criterion "do not rely on regex rejection alone" is satisfied
/// structurally rather than textually. The callback sees the <em>resolved</em> statement, not its
/// text: a column reached through a join, an alias, a correlated subquery, a view, a <c>CASE</c>
/// expression or a <c>WITH</c> clause arrives here as the same <c>SQLITE_READ</c> on the same
/// physical table and column as a direct reference, and a table the statement never names but a
/// subquery reaches is reported all the same. There is no spelling of a forbidden read that gets
/// past it, because the thing being checked is the access, not the syntax.</para>
///
/// <para>Everything is denied unless it is listed. A new table, a new column on a listed table, a
/// new SQLite function, a <c>PRAGMA</c>, an <c>ATTACH</c>, a write and every DDL statement all fall
/// through to <see cref="Deny"/> without this file changing, which is exactly the "denied
/// automatically until a separately reviewed allow-list change" behaviour the issue requires.</para>
///
/// <para>A denial fails preparation with <c>SQLITE_AUTH</c>, so the statement never runs at all -
/// not partially, and not with a substituted value. That is deliberate: returning
/// <c>SQLITE_IGNORE</c> would silently produce <c>NULL</c> where a forbidden column was read, and a
/// diagnostics result that quietly differs from the query asked is worse than a refusal.</para>
/// </summary>
internal sealed class SqliteDiagnosticsAuthorizer
{
    /// <summary>
    /// The only schema a query may touch. <c>temp</c> is excluded because a temp object would be
    /// something the statement created, and <c>ATTACH</c> is refused outright, so any other name
    /// here means the statement reached somewhere it has no business being.
    /// </summary>
    private const string PermittedSchemaName = "main";

    /// <summary>
    /// The column whose value a rowid read yields on this schema. Every table on the permitted
    /// surface has an <c>INTEGER PRIMARY KEY</c> named <c>Id</c>, which SQLite stores as the rowid
    /// itself, so a rowid read returns exactly what this column already exposes - and must be
    /// permitted only while it does.
    /// </summary>
    private const string RowIdPrimaryKeyColumnName = "Id";

    /// <summary>
    /// The SQL functions a diagnostics query may call: counting and aggregating rows, comparing
    /// them, and handling nulls. That is what an orphan or cross-business ownership investigation
    /// over seven tables of identity and foreign-key columns needs.
    ///
    /// Everything else is denied, including the ones that matter most:
    /// <c>load_extension</c> (loads native code), <c>readfile</c>/<c>writefile</c> (filesystem
    /// access), <c>zeroblob</c>/<c>randomblob</c> (unbounded allocation), the
    /// <c>sqlite_version</c>/<c>sqlite_compileoption_*</c> introspection family, and the
    /// <c>like</c>/<c>glob</c>/<c>regexp</c> pattern functions, which the surface's integer columns
    /// have no use for.
    /// </summary>
    private static readonly HashSet<string> PermittedFunctions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "abs",
            "avg",
            "coalesce",
            "count",
            "group_concat",
            "ifnull",
            "iif",
            "length",
            "max",
            "min",
            "nullif",
            "round",
            "sum",
            "total",
            "typeof",
        };

    /// <summary>
    /// The first refusal, kept so the caller can say what was denied without echoing the
    /// statement. Only the first is kept: preparation stops there, and a list would be noise.
    /// </summary>
    public string? DeniedDescription { get; private set; }

    /// <summary>
    /// Whether the refusal was about reaching outside the permitted data surface, as opposed to
    /// attempting an operation the connection does not allow at all. The two map to different
    /// denial reasons, because "that column is not on the surface" and "this connection does not
    /// run PRAGMAs" are different answers for an operator to read.
    /// </summary>
    public bool DeniedSchemaAccess { get; private set; }

    /// <summary>
    /// The callback itself. <paramref name="actionCode"/> selects the meaning of the two name
    /// arguments; every code other than the three handled here is refused by falling through.
    /// </summary>
    public int Authorize(
        object? userData,
        int actionCode,
        utf8z firstArgument,
        utf8z secondArgument,
        utf8z databaseName,
        utf8z triggerOrView)
    {
        _ = userData;
        _ = triggerOrView;

        if (actionCode == raw.SQLITE_SELECT)
        {
            // The statement is a read. What it reads is decided one SQLITE_READ at a time below.
            return raw.SQLITE_OK;
        }

        if (actionCode == raw.SQLITE_READ)
        {
            return AuthorizeRead(
                firstArgument.utf8_to_string(),
                secondArgument.utf8_to_string(),
                databaseName.utf8_to_string());
        }

        if (actionCode == raw.SQLITE_FUNCTION)
        {
            var function = secondArgument.utf8_to_string();

            return PermittedFunctions.Contains(function ?? string.Empty)
                ? raw.SQLITE_OK
                : Deny($"The SQL function '{Describe(function)}' is not permitted.", schemaAccess: false);
        }

        return Deny(
            $"The requested SQLite operation (action code {actionCode}) is not permitted on the "
                + "read-only diagnostics connection.",
            schemaAccess: false);
    }

    private int AuthorizeRead(string? table, string? column, string? databaseName)
    {
        if (!PlatformDiagnosticsDataSurface.PermitsTable(table))
        {
            return Deny(
                $"The table '{Describe(table)}' is outside the permitted diagnostics data surface.",
                schemaAccess: true);
        }

        // SQLite reports a read with no column name when the plan touches the table without
        // naming a column at all - an existence test or a bare row count. It reports no schema
        // name for these either, which is why the schema check below comes after this branch
        // rather than before it.
        //
        // What such a read can yield is the row's rowid, so it is permitted only when the table's
        // Id column is itself on the surface. That keeps the branch honest if a future allow-list
        // entry ever lists a table without its Id: a bare table reference must not become a way to
        // read a key the surface withheld.
        //
        // The explicit spellings "rowid", "oid" and "_rowid_" are deliberately *not* treated as
        // this case. SQLite resolves them against the table's declared columns first: they mean the
        // internal row id only while no real column carries that name, and the moment a migration
        // adds one (ALTER TABLE Products ADD COLUMN oid TEXT) the same spelling becomes that
        // column. Permitting the names would therefore hand out a future unlisted column, which is
        // exactly what "a new column is denied until a reviewed allow-list change" forbids. Nothing
        // legitimate is lost: when a rowid reference does resolve to the internal row id, SQLite
        // reports it to this callback under the name of the table's INTEGER PRIMARY KEY - "Id" on
        // every table of this surface - so "SELECT rowid FROM Products" arrives here as
        // "Products.Id" and is allowed on its own merits.
        if (string.IsNullOrEmpty(column))
        {
            return PlatformDiagnosticsDataSurface.Permits(table, RowIdPrimaryKeyColumnName)
                ? raw.SQLITE_OK
                : Deny(
                    $"The row identifier of '{Describe(table)}' is outside the permitted "
                        + "diagnostics data surface.",
                    schemaAccess: true);
        }

        if (!string.Equals(databaseName, PermittedSchemaName, StringComparison.OrdinalIgnoreCase))
        {
            return Deny(
                $"Reading '{Describe(table)}.{Describe(column)}' from the "
                    + $"'{Describe(databaseName)}' schema is not permitted.",
                schemaAccess: true);
        }

        return PlatformDiagnosticsDataSurface.Permits(table, column)
            ? raw.SQLITE_OK
            : Deny(
                $"The column '{Describe(table)}.{Describe(column)}' is outside the permitted "
                    + "diagnostics data surface.",
                schemaAccess: true);
    }

    private int Deny(string description, bool schemaAccess)
    {
        if (DeniedDescription is null)
        {
            DeniedDescription = description;
            DeniedSchemaAccess = schemaAccess;
        }

        return raw.SQLITE_DENY;
    }

    /// <summary>
    /// Names in a refusal message come from the statement, so they are reported as the schema
    /// identifiers they are and nothing else: no value, no row, and no part of the submitted SQL
    /// beyond the one identifier that was refused.
    /// </summary>
    private static string Describe(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "(unnamed)" : name;
}
