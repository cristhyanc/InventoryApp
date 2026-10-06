namespace Inventory.Infrastructure.PlatformDiagnostics;

/// <summary>
/// What the composition root has to tell the diagnostics adapter: which database to read
/// (issue #336).
///
/// It is only the connection string, because that is the only part of this the composition root
/// owns. Choosing the provider and the data source stays a <c>Program.cs</c> decision exactly as
/// it is for <c>AppDbContext</c>; forcing the connection open read-only, refusing attached
/// databases, limiting the statement and restricting the data surface are enforcement, and
/// enforcement belongs to the adapter so no host can be configured out of it.
/// </summary>
public sealed class SqliteDiagnosticsOptions
{
    public string ConnectionString { get; set; } = string.Empty;
}
