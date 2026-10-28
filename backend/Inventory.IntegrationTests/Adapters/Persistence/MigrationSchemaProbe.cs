using Microsoft.Data.Sqlite;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Reads the shape of the SQLite schema a migration upgrade test is standing on: which tables
/// exist, and which columns a table has.
///
/// Every migration upgrade test asks those two questions, and each one used to carry its own
/// identical copy of the two queries. One shared copy is not only less to read: it is also the only
/// way a change to how the schema is probed - a quoting fix, a different ordering - cannot end up
/// true in one upgrade test and false in the next.
/// </summary>
internal static class MigrationSchemaProbe
{
    /// <summary>Every column of <paramref name="table"/>, in the order SQLite declares them.</summary>
    public static Task<List<string>> ColumnNamesAsync(SqliteConnection connection, string table) =>
        QueryNamesAsync(connection, $"SELECT name FROM pragma_table_info('{table}');");

    /// <summary>
    /// The <c>CREATE TABLE</c> statement SQLite holds for <paramref name="table"/>, exactly as the
    /// schema declares it.
    ///
    /// Asked when the question is about a declaration no pragma reports - whether a key is
    /// <c>INTEGER PRIMARY KEY AUTOINCREMENT</c>, and therefore assigned by the database (issue
    /// #735), is one of those: <c>pragma_table_info</c> says the column is the primary key but not
    /// that SQLite fills it in.
    /// </summary>
    public static async Task<string> CreateTableSqlAsync(SqliteConnection connection, string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT sql FROM sqlite_master WHERE type = 'table' AND name = '{table}';";
        return await command.ExecuteScalarAsync() is string sql
            ? sql
            : throw new InvalidOperationException($"The database holds no table named {table}.");
    }

    /// <summary>Every table in the database, ordered by name.</summary>
    public static Task<List<string>> TableNamesAsync(SqliteConnection connection) =>
        QueryNamesAsync(connection, "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;");

    /// <summary>
    /// Every index declared on <paramref name="table"/>, ordered by name. Asked by a migration
    /// that adds or removes an index, for the same reason the column probe exists: the schema, not
    /// the model, is what the test is standing on.
    /// </summary>
    public static Task<List<string>> IndexNamesAsync(SqliteConnection connection, string table) =>
        QueryNamesAsync(
            connection,
            "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = "
                + $"'{table}' ORDER BY name;");

    /// <summary>
    /// The default expression SQLite holds for <paramref name="column"/> of
    /// <paramref name="table"/>, exactly as the schema declares it, or <see langword="null"/> when
    /// the column has no default at all.
    ///
    /// Asked by a migration whose point *is* the default (issue #584): what a row gets when an
    /// insert states no value is a property of the schema, not of the model, so the only way to
    /// prove it is to read it back from the database.
    /// </summary>
    public static async Task<string?> ColumnDefaultAsync(
        SqliteConnection connection,
        string table,
        string column)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT dflt_value FROM pragma_table_info('{table}') WHERE name = '{column}';";
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : (string)value;
    }

    /// <summary>
    /// How many rows <paramref name="table"/> holds, read over the connection rather than through
    /// the EF model.
    ///
    /// Asked by an upgrade test whose database deliberately stops short of the latest migration: the
    /// model describes the schema <em>after</em> every migration, so a <c>DbSet</c> read against an
    /// earlier schema fails on a column that does not exist yet - while "this table arrived empty"
    /// is a claim about the rows and not about the columns.
    /// </summary>
    public static async Task<long> RowCountAsync(SqliteConnection connection, string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<List<string>> QueryNamesAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();

        var values = new List<string>();
        while (await reader.ReadAsync()) values.Add(reader.GetString(0));
        return values;
    }
}
