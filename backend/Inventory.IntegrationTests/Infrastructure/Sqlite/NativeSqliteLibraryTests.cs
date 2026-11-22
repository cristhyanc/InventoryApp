using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace InventoryApi.Tests.Infrastructure.Sqlite;

/// <summary>
/// The native SQLite engine the backend actually runs on (issue #1290).
///
/// <para><strong>Why this is pinned at all.</strong> SQLite is the production database, so the
/// engine version is a deployment fact, not an implementation detail: it decides which SQLite fixes
/// and behaviours the API, the migrations, the diagnostics authorizer and the backup API run with.
/// Before this issue, <c>InventoryApi.csproj</c> asked for <c>SQLitePCLRaw.lib.e_sqlite3</c> through
/// the open prerelease range <c>[2.1.12-pre20260709125052, )</c>, so production ran a prerelease
/// native library and a restore would have broken the day that prerelease was unlisted. Every
/// SQLitePCLRaw package is now pinned to one stable version instead.</para>
///
/// <para><strong>What this proves.</strong> The engine loaded into the test process reports a
/// version no older than the prerelease's <c>3.53.3</c>, so pinning a stable package did not quietly
/// move the database engine backwards, and the pinned native package - not an unrelated system
/// library - is what ships next to the managed assemblies. The reported version is written to the
/// test output, which is where the pull request's figure comes from.</para>
/// </summary>
public sealed class NativeSqliteLibraryTests
{
    /// <summary>
    /// The floor this issue pins: the native SQLite version bundled by the prerelease package the
    /// backend referenced before it. A stable pin may move the engine forward, never back.
    /// </summary>
    private static readonly Version MinimumNativeSqliteVersion = new(3, 53, 3);

    private readonly ITestOutputHelper _output;

    public NativeSqliteLibraryTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void The_native_sqlite_engine_is_not_older_than_the_pinned_minimum()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version();";
        var reported = command.ExecuteScalar() as string;

        _output.WriteLine($"SELECT sqlite_version() reports {reported}");

        Assert.False(string.IsNullOrWhiteSpace(reported));

        var actual = Version.Parse(reported!);
        Assert.True(
            actual >= MinimumNativeSqliteVersion,
            $"The native SQLite engine reports {reported}, which is older than the pinned minimum " +
            $"{MinimumNativeSqliteVersion}. Align every SQLitePCLRaw package on a stable version whose " +
            "bundled native SQLite is at least that version instead of lowering this floor.");
    }

    /// <summary>
    /// The native file names SQLitePCLRaw's <c>lib.e_sqlite3</c> package ships per platform. The
    /// managed <c>SQLitePCLRaw.provider.e_sqlite3</c> assembly is deliberately not one of them: it
    /// only P/Invokes into one of these.
    /// </summary>
    private static readonly string[] NativeLibraryFileNames =
        ["libe_sqlite3.so", "libe_sqlite3.dylib", "e_sqlite3.dll"];

    [Fact]
    public void The_e_sqlite3_native_library_ships_beside_the_managed_assemblies()
    {
        var nativeLibraries = Directory
            .EnumerateFiles(AppContext.BaseDirectory, "*e_sqlite3*", SearchOption.AllDirectories)
            .Where(path => NativeLibraryFileNames.Contains(
                Path.GetFileName(path),
                StringComparer.OrdinalIgnoreCase))
            .ToList();

        foreach (var library in nativeLibraries)
        {
            _output.WriteLine(Path.GetRelativePath(AppContext.BaseDirectory, library));
        }

        Assert.NotEmpty(nativeLibraries);
    }
}
