using System.Text.Json;

namespace InventoryApi.Tests.Auth;

/// <summary>
/// Pins the content root <c>WebApplicationFactory&lt;Program&gt;</c> hosts the API with, so hosting
/// it does not depend on the process's current directory.
///
/// <para>
/// Why this is needed: <c>Microsoft.AspNetCore.Mvc.Testing</c> infers the content root by reading
/// <c>MvcTestingAppManifest.json</c> <em>relative to the current directory</em>, and falls back to
/// searching parent directories for a <c>*.sln</c> file - which this repository does not have, it
/// has <c>InventoryApi.slnx</c> - and throws when that fails. Another test in this assembly
/// legitimately changes the process directory while it runs
/// (<c>Operations.SqliteBackupRestoreTests</c> restores a database from a temporary directory), so
/// a host started while it holds the directory cannot find the manifest and fails with
/// "Solution root could not be located". That is a function of test interleaving, not of anything
/// the host under test does.
/// </para>
///
/// <para>
/// What it does: reads the same manifest, by absolute path next to the test assembly, and publishes
/// the path it names through the <c>ASPNETCORE_TEST_CONTENTROOT_INVENTORYAPI</c> environment
/// variable, which is the hosting layer's own documented override and is checked before any
/// probing. The value is therefore exactly the path the manifest would have produced; this only
/// makes finding it deterministic. Setting it is idempotent, and it is deliberately not reverted:
/// every host in this process wants the same answer.
/// </para>
/// </summary>
internal static class E2ETestHostContentRoot
{
    private const string EnvironmentVariableName = "ASPNETCORE_TEST_CONTENTROOT_INVENTORYAPI";
    private const string ManifestFileName = "MvcTestingAppManifest.json";
    private const string ApiAssemblyName = "InventoryApi";

    private static readonly object Gate = new();
    private static bool _pinned;

    public static void Pin()
    {
        lock (Gate)
        {
            if (_pinned)
            {
                return;
            }

            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(EnvironmentVariableName)))
            {
                Environment.SetEnvironmentVariable(EnvironmentVariableName, Resolve());
            }

            _pinned = true;
        }
    }

    /// <summary>
    /// The API project directory the build recorded in the manifest, or the test output directory
    /// when the manifest is missing or does not name the API - which still holds the same
    /// appsettings files, and is a real directory, so hosting succeeds either way.
    /// </summary>
    private static string Resolve()
    {
        var manifestPath = Path.Combine(AppContext.BaseDirectory, ManifestFileName);

        if (File.Exists(manifestPath))
        {
            var manifest = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(manifestPath));
            var apiEntry = manifest?.FirstOrDefault(entry =>
                entry.Key.StartsWith($"{ApiAssemblyName},", StringComparison.Ordinal));

            if (apiEntry is { Value: { } contentRoot } && Directory.Exists(contentRoot))
            {
                return contentRoot;
            }
        }

        return AppContext.BaseDirectory;
    }
}
