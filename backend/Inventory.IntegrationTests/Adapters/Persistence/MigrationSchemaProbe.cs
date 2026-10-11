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
