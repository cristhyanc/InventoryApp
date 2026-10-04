using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace InventoryApi.Tests.Operations;

/// <summary>
/// Issue #333: the scheduled database backup WebJob. The job itself contains no backup logic - it
/// invokes the published executable's <c>backup-database --upload</c> command (issues #331, #332) -
/// so what has to be proven here is the packaging and the invocation:
///
/// <list type="number">
/// <item><description>
/// The two WebJob files exist where App Service expects a triggered WebJob
/// (<c>App_Data/jobs/triggered/&lt;job name&gt;/</c> under the deployed site root) and are declared
/// as publish content, so <c>dotnet publish</c> of <c>InventoryApi</c> carries them into the output
/// directory the <b>Deploy Production</b> workflow deploys whole.
/// </description></item>
/// <item><description>
/// The schedule is the daily 15:00 UTC one the issue proposed, expressed in the six-field CRON
/// form <c>settings.job</c> uses.
/// </description></item>
/// <item><description>
/// The script invokes the supported upload command, propagates its exit code, logs start,
/// completion/failure and duration, and never names a database path of its own - the database it
/// backs up is whatever <c>ConnectionStrings:DefaultConnection</c> resolves to for the API.
/// </description></item>
/// </list>
///
/// The publish assertions read the project file and the repository rather than running
/// <c>dotnet publish</c>: the validation pipeline runs the test suite with <c>--no-build</c>, so a
/// test may not invoke the SDK. What is asserted instead is the exact mechanism publish uses - a
/// <c>Content</c> item with <c>CopyToPublishDirectory</c>, whose path relative to the project is
/// the path the file takes inside the publish directory.
///
/// The behavioural assertions run the real script under <c>bash</c> against a fake <c>dotnet</c> on
/// <c>PATH</c> and a throwaway application directory, so no backup, upload, Azure call or database
/// access happens here.
/// </summary>
public class BackupWebJobPackagingTests
{
    /// <summary>The WebJob's name, which is its directory name under <c>App_Data/jobs/triggered</c>.</summary>
    private const string JobName = "database-backup";

    /// <summary>Daily at 15:00 UTC, in the six-field (second minute hour day month weekday) CRON form.</summary>
    private const string ExpectedSchedule = "0 0 15 * * *";

    private static readonly string JobDirectoryRelativePath =
        Path.Combine("App_Data", "jobs", "triggered", JobName);

    [Fact]
    public void The_webjob_ships_a_run_script_and_a_schedule()
    {
        Assert.True(File.Exists(RunScriptPath()), $"Expected the WebJob script at {RunScriptPath()}.");
        Assert.True(File.Exists(SettingsJobPath()), $"Expected the WebJob schedule at {SettingsJobPath()}.");
    }

    /// <summary>
    /// App Service runs <c>run.sh</c> through bash, so a CRLF checkout would fail on the first line.
    /// <c>.gitattributes</c> pins the job's files to LF; this asserts the file as checked out.
    /// </summary>
    [Fact]
    public void The_run_script_uses_unix_line_endings()
    {
        var script = File.ReadAllText(RunScriptPath());

        Assert.DoesNotContain("\r", script, StringComparison.Ordinal);
    }

    /// <summary>
    /// The Web SDK's default content globs cover <c>wwwroot</c>, <c>*.config</c> and <c>*.json</c>
    /// only, so neither <c>run.sh</c> nor the extensionless-by-convention <c>settings.job</c> is
    /// published unless the project says so. Without this declaration the deployment carries no
    /// WebJob at all, which is the one packaging failure that would be invisible at run time until
    /// a backup silently never happened.
    /// </summary>
    [Fact]
    public void The_webjob_files_are_declared_as_publish_content_of_the_api_project()
    {
        var contentItems = XDocument.Load(ApiProjectFile())
            .Descendants("Content")
            .ToArray();

        foreach (var fileName in new[] { "run.sh", "settings.job" })
        {
            var relativePath = Normalize(Path.Combine(JobDirectoryRelativePath, fileName));

            var publishing = contentItems
                .Where(item => MatchesInclude(item.Attribute("Include")?.Value, relativePath))
                .ToArray();

            Assert.True(
                publishing.Length > 0,
                $"InventoryApi.csproj declares no Content item covering {relativePath}, so "
                    + "dotnet publish would not carry the WebJob into the deployed output.");

            Assert.All(
                publishing,
                item => Assert.Contains(
                    CopyToPublishDirectory(item),
                    new[] { "Always", "PreserveNewest" }));
        }
    }

    /// <summary>
    /// The schedule is part of the deployment artifact rather than portal configuration, so it is
    /// reviewable and it survives a redeploy. 15:00 UTC is the issue's proposed window, and the
    /// 36-hour missing-backup alert of issue #334 is specified against this daily cadence.
    /// </summary>
    [Fact]
    public void The_schedule_is_daily_at_1500_utc()
    {
        using var settings = JsonDocument.Parse(File.ReadAllText(SettingsJobPath()));

        var schedule = settings.RootElement.GetProperty("schedule").GetString();

        Assert.Equal(ExpectedSchedule, schedule);

        // Six fields, not five: settings.job CRON starts at seconds, so a five-field expression
        // would silently mean something else (15 would become the minute).
        var fields = ExpectedSchedule.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(6, fields.Length);
        Assert.Equal("0", fields[0]);
        Assert.Equal("0", fields[1]);
        Assert.Equal("15", fields[2]);
    }

    /// <summary>
    /// The job must invoke the one supported command and must not reimplement or reconfigure any
    /// part of it: no retained-snapshot path, no database path, and no connection string. The
    /// database comes from the application's own configuration, so an operator who moves the
    /// database by changing <c>ConnectionStrings__DefaultConnection</c> moves the backup with it.
    ///
    /// Comment lines are excluded deliberately: the script's comments explain the configured
    /// default it relies on, and explaining a path is not the same as naming one in the command.
    /// </summary>
    [Fact]
    public void The_run_script_invokes_the_upload_command_and_names_no_database_path()
    {
        var executableLines = string.Join(
            '\n',
            File.ReadAllLines(RunScriptPath())
                .Where(line => !line.TrimStart().StartsWith('#')));

        Assert.Contains("backup-database", executableLines, StringComparison.Ordinal);
        Assert.Contains("--upload", executableLines, StringComparison.Ordinal);

        Assert.DoesNotContain("--output", executableLines, StringComparison.Ordinal);
        Assert.DoesNotContain("inventory.db", executableLines, StringComparison.Ordinal);
        Assert.DoesNotContain("/home/data", executableLines, StringComparison.Ordinal);
        Assert.DoesNotContain("ConnectionStrings", executableLines, StringComparison.Ordinal);
        Assert.DoesNotContain("BackupStorage", executableLines, StringComparison.Ordinal);
        Assert.DoesNotContain("Data Source=", executableLines, StringComparison.Ordinal);
    }

    [Fact]
    public void A_successful_run_invokes_the_upload_command_from_the_application_directory_and_exits_zero()
    {
        if (!CanRunShellScripts)
        {
            return;
        }

        using var sandbox = JobSandbox.Create(dotnetExitCode: 0);

        var run = sandbox.RunJob();

        Assert.Equal(0, run.ExitCode);

        // The command is invoked with no argument beyond the two the mode needs, and from the
        // application's own directory, so a relative "Data Source=inventory.db" default resolves
        // exactly where it resolves for the running API.
        Assert.Equal("InventoryApi.dll backup-database --upload", sandbox.RecordedArguments());
        Assert.Equal(sandbox.ApplicationDirectory, sandbox.RecordedWorkingDirectory());
        Assert.Equal("<unset>", sandbox.RecordedConnectionStringSetting());

        Assert.Contains("START", run.Output, StringComparison.Ordinal);
        Assert.Contains("COMPLETED", run.Output, StringComparison.Ordinal);
        Assert.Matches(@"duration \d+s", run.Output);
        Assert.DoesNotContain("FAILED (", run.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Exit code is the whole contract between the command and the schedule: a failed run must be a
    /// failed WebJob run, visible in the job's history, not a green run with a bad log line.
    /// </summary>
    [Fact]
    public void A_failed_command_fails_the_job_with_the_commands_own_exit_code()
    {
        if (!CanRunShellScripts)
        {
            return;
        }

        using var sandbox = JobSandbox.Create(dotnetExitCode: 1);

        var run = sandbox.RunJob();

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("FAILED (", run.Output, StringComparison.Ordinal);
        Assert.Matches(@"duration \d+s", run.Output);
    }

    /// <summary>
    /// A job that cannot find the published application has not taken a backup, so it must fail
    /// rather than report a run that did nothing. The failure line carries the same
    /// <c>FAILED (reason)</c> shape the command's own failures use, so the failed-run alert of
    /// issue #334 catches a job-level failure as well as a command-level one.
    /// </summary>
    [Fact]
    public void A_missing_application_directory_fails_the_job_without_invoking_anything()
    {
        if (!CanRunShellScripts)
        {
            return;
        }

        using var sandbox = JobSandbox.Create(dotnetExitCode: 0);

        var run = sandbox.RunJob(homeOverride: sandbox.EmptyDirectory);

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("FAILED (", run.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(sandbox.InvocationLogPath), "The backup command must not have been invoked.");
    }

    /// <summary>
    /// The shell assertions need a POSIX shell, <c>chmod</c>-style permissions and a process
    /// environment the test controls. Windows PowerShell validation runs the same suite, where
    /// those are not available, and the packaging/schedule/content assertions above already cover
    /// the parts of this change that are platform independent.
    /// </summary>
    private static bool CanRunShellScripts => !OperatingSystem.IsWindows();

    private static string CopyToPublishDirectory(XElement content) =>
        content.Attribute("CopyToPublishDirectory")?.Value
        ?? content.Element("CopyToPublishDirectory")?.Value
        ?? "(not set)";

    /// <summary>
    /// True when an MSBuild <c>Include</c> value covers <paramref name="relativePath"/>: either the
    /// exact path, or a recursive glob whose fixed prefix the path starts with. Only the two forms
    /// a project would plausibly use for these files are recognised, so a declaration this cannot
    /// read is reported as missing rather than assumed to work.
    /// </summary>
    private static bool MatchesInclude(string? include, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(include))
        {
            return false;
        }

        var normalized = Normalize(include);

        if (string.Equals(normalized, relativePath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var recursiveWildcard = normalized.IndexOf("**", StringComparison.Ordinal);
        return recursiveWildcard >= 0
            && relativePath.StartsWith(normalized[..recursiveWildcard], StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path) =>
        path.Replace('\\', '/').TrimStart('.', '/');

    private static string RunScriptPath() => Path.Combine(JobDirectory(), "run.sh");

    private static string SettingsJobPath() => Path.Combine(JobDirectory(), "settings.job");

    private static string JobDirectory() =>
        Path.Combine(ApiProjectDirectory(), JobDirectoryRelativePath);

    private static string ApiProjectFile() =>
        Path.Combine(ApiProjectDirectory(), "InventoryApi.csproj");

    private static string ApiProjectDirectory()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !Directory.Exists(Path.Combine(current.FullName, "InventoryApi")))
        {
            current = current.Parent;
        }

        var backendRoot = current?.FullName
            ?? throw new InvalidOperationException("Could not locate the backend directory containing InventoryApi.");

        return Path.Combine(backendRoot, "InventoryApi");
    }

    /// <summary>
    /// A throwaway environment for running the real <c>run.sh</c>: a fake <c>dotnet</c> first on
    /// <c>PATH</c> that records how it was called and exits with a chosen code, and a
    /// <c>HOME</c> whose <c>site/wwwroot</c> holds an empty <c>InventoryApi.dll</c> - the same
    /// layout a Linux App Service deployment produces, so the job's own lookup is exercised rather
    /// than bypassed.
    /// </summary>
    private sealed class JobSandbox : IDisposable
    {
        private readonly string _root;

        private JobSandbox(string root)
        {
            _root = root;
        }

        /// <summary>Where the published application sits, relative to the sandbox's <c>HOME</c>.</summary>
        public string ApplicationDirectory => Path.Combine(_root, "site", "wwwroot");

        /// <summary>A <c>HOME</c> with no deployed application under it at all.</summary>
        public string EmptyDirectory => Path.Combine(_root, "empty");

        public string InvocationLogPath => Path.Combine(_root, "invocation.log");

        public static JobSandbox Create(int dotnetExitCode)
        {
            var root = Path.Combine(Path.GetTempPath(), "inventoryapp-webjob-tests", Guid.NewGuid().ToString("N"));
            var sandbox = new JobSandbox(root);

            Directory.CreateDirectory(sandbox.ApplicationDirectory);
            Directory.CreateDirectory(sandbox.EmptyDirectory);
            Directory.CreateDirectory(Path.Combine(root, "bin"));

            // Stands in for the published application. The job only ever checks that the entry
            // assembly is there and hands the work to dotnet, so an empty file is enough and no
            // real assembly, database or Azure destination is involved.
            File.WriteAllText(Path.Combine(sandbox.ApplicationDirectory, "InventoryApi.dll"), string.Empty);

            var shim = Path.Combine(root, "bin", "dotnet");
            File.WriteAllText(
                shim,
                $$"""
                #!/usr/bin/env bash
                {
                  printf 'cwd=%s\n' "$PWD"
                  printf 'args=%s\n' "$*"
                  printf 'connection=%s\n' "${ConnectionStrings__DefaultConnection:-<unset>}"
                } >> "{{sandbox.InvocationLogPath}}"
                exit {{dotnetExitCode}}

                """);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    shim,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            return sandbox;
        }

        public ShellRun RunJob(string? homeOverride = null)
        {
            var startInfo = new ProcessStartInfo("bash")
            {
                WorkingDirectory = _root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            startInfo.ArgumentList.Add(RunScriptPath());

            startInfo.Environment["PATH"] =
                Path.Combine(_root, "bin") + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
            startInfo.Environment["HOME"] = homeOverride ?? _root;
            startInfo.Environment.Remove("WEBROOT_PATH");
            startInfo.Environment.Remove("ConnectionStrings__DefaultConnection");

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("bash did not start.");

            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();

            return new ShellRun(process.ExitCode, output);
        }

        public string RecordedArguments() => RecordedValue("args");

        public string RecordedWorkingDirectory() => RecordedValue("cwd");

        public string RecordedConnectionStringSetting() => RecordedValue("connection");

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch (IOException)
            {
                // A leftover temporary directory never fails a test.
            }
        }

        private string RecordedValue(string key)
        {
            Assert.True(File.Exists(InvocationLogPath), "The backup command was never invoked.");

            var line = File.ReadAllLines(InvocationLogPath)
                .FirstOrDefault(entry => entry.StartsWith(key + "=", StringComparison.Ordinal))
                ?? throw new InvalidOperationException($"The invocation log recorded no '{key}' value.");

            return line[(key.Length + 1)..];
        }
    }

    private sealed record ShellRun(int ExitCode, string Output);
}
