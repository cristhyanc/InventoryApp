using System.Reflection;
using System.Text.RegularExpressions;
using Inventory.Application.Nayax;
using NetArchTest.Rules;
using Xunit;

namespace InventoryApi.Tests.Architecture;

/// <summary>
/// The re-test credential boundary of issue #520, enforced against the compiled assemblies.
///
/// <see cref="INayaxConnectionRetestCredentialProvider"/> deliberately ignores the status gate, so
/// that ordinary Nayax consumers cannot call it is a security property rather than a convention: a
/// consumer that could reach it would have a way to use credentials the gate refuses, including a
/// connection an operator has been told needs attention. The parent contract (#500) asks for an
/// architecture or unit test that proves it, and this is it - a review habit would not survive the
/// next person who needs a token.
///
/// The allow-list is exactly four types: the port, its one implementation, the composition that
/// registers them, and - since issue #537 - the Owner-only re-test use case this boundary was
/// always for, added as the conscious, reviewed edit this comment anticipated. Nothing else may
/// join it.
///
/// <para><strong>Since issue #681 the gate-free <em>read</em> is covered too.</strong> The type-level
/// rules above guard the gate-free credential <em>provider</em>, but the read that actually skips the
/// status gate is <see cref="INayaxConnectionStore.FindCredentialAsync"/> - a method on the ordinary
/// connection store, which every Nayax consumer legitimately depends on. A type-level dependency
/// rule cannot see a method call, so the boundary that mattered most was the one nothing asserted:
/// any use case could have called that method directly and used a credential the gate refuses. The
/// rule below is checked against the source text instead, using the same precedent
/// <see cref="TimeAcquisitionTests"/> established for a property read a dependency rule cannot
/// distinguish.</para>
/// </summary>
public class NayaxRetestCredentialBoundaryTests
{
    private const string InstrumentationNamespacePrefix = "Coverlet";

    private static readonly string RetestPort = typeof(INayaxConnectionRetestCredentialProvider).FullName!;
    private static readonly string RetestProvider = typeof(NayaxConnectionRetestCredentialProvider).FullName!;

    private static readonly string[] AllowedDependents =
    [
        "Inventory.Application.Nayax.INayaxConnectionRetestCredentialProvider",
        "Inventory.Application.Nayax.NayaxConnectionRetestCredentialProvider",
        // The composition root of Inventory.Application: it registers the port and must name it.
        "Inventory.Application.ApplicationServiceCollectionExtensions",
        // The Owner-only re-test of issue #537, which is the one consumer this boundary was always
        // for: a connection in NeedsAttention is exactly the one an operator has to recover, and
        // the gate that refuses ordinary calls in that state would make recovery impossible. It
        // writes its result back through the store's conditional, revision-bound update, so a
        // credential replaced while it ran keeps its own status.
        "Inventory.Application.Nayax.RetestNayaxConnection",
    ];

    private static readonly (string Layer, Assembly Assembly)[] ProductionAssemblies =
    [
        ("Inventory.Application", typeof(Inventory.Application.AssemblyMarker).Assembly),
        ("Inventory.Infrastructure", typeof(Inventory.Infrastructure.AssemblyMarker).Assembly),
        ("InventoryApi", typeof(Program).Assembly),
    ];

    [Fact]
    public void No_ordinary_consumer_may_depend_on_the_gate_free_re_test_port()
    {
        foreach (var (layer, assembly) in ProductionAssemblies)
        {
            AssertOnlyAllowedDependents(layer, assembly, RetestPort);
            AssertOnlyAllowedDependents(layer, assembly, RetestProvider);
        }
    }

    /// <summary>
    /// Proves the rule above can actually see a dependent, rather than passing because the
    /// dependency analysis found nothing at all: the two types that legitimately name the port are
    /// detected, and are passing only because they are on the allow-list.
    /// </summary>
    [Fact]
    public void The_dependency_rule_detects_the_types_that_legitimately_name_the_port()
    {
        var detected = Dependents(typeof(Inventory.Application.AssemblyMarker).Assembly, RetestPort);

        Assert.Contains(RetestProvider, detected);
        Assert.Contains("Inventory.Application.ApplicationServiceCollectionExtensions", detected);
    }

    /// <summary>
    /// The Nayax HTTP client is the one adapter every ordinary consumer reaches Nayax through, so it
    /// is named explicitly: if it ever gained the gate-free read, every consumer would have it.
    /// </summary>
    [Fact]
    public void The_nayax_http_client_depends_only_on_the_gated_provider()
    {
        var clientDependencies = Types
            .InAssembly(typeof(Inventory.Infrastructure.Nayax.NayaxLynxClient).Assembly)
            .That()
            .HaveName(nameof(Inventory.Infrastructure.Nayax.NayaxLynxClient))
            .Should()
            .HaveDependencyOn(typeof(INayaxRequestCredentialProvider).FullName)
            .GetResult();

        Assert.True(
            clientDependencies.IsSuccessful,
            "NayaxLynxClient must resolve its credentials from INayaxRequestCredentialProvider, the "
                + "gated port, so the status gate applies to every ordinary Nayax call.");
    }

    /// <summary>
    /// Freezes the gated port's surface. The separation only holds while the gated provider has no
    /// gate-free member of its own, and "add one small method here" is exactly how such a boundary
    /// is lost.
    /// </summary>
    [Fact]
    public void The_gated_provider_exposes_only_its_two_declared_members()
    {
        var members = typeof(INayaxRequestCredentialProvider)
            .GetMethods()
            .Select(method => method.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                nameof(INayaxRequestCredentialProvider.GetForOperationAsync),
                nameof(INayaxRequestCredentialProvider.ReportUnauthorizedAsync),
            },
            members);
    }

    #region The gate-free read itself (issue #681)

    /// <summary>
    /// The production source files allowed to name <c>FindCredentialAsync</c> at all. Each one is
    /// here for a stated reason, and adding a line is the reviewed edit:
    ///
    /// <list type="bullet">
    ///   <item><c>INayaxConnectionStore.cs</c> - declares it, and documents why it is separate from
    ///   the gated <c>FindForOperationAsync</c>.</item>
    ///   <item><c>EfNayaxConnectionStore.cs</c> - implements it.</item>
    ///   <item><c>NayaxConnectionRetestCredentialProvider.cs</c> - the gate-free provider, which is
    ///   exactly this read behind the port the type-level rules above guard.</item>
    ///   <item><c>NayaxConnectionMigrator.cs</c> - the human-invoked <c>migrate-nayax-connection</c>
    ///   command, which compares the stored credential with the one it is migrating. It has to read
    ///   the token whatever the status says, because a connection in <c>NeedsAttention</c> is
    ///   precisely one an operator may be re-running this command for; it makes no Nayax call with
    ///   it.</item>
    ///   <item><c>NayaxConnection.cs</c>, <c>NayaxConnectionSnapshot.cs</c>,
    ///   <c>NayaxConnectionCredential.cs</c>, <c>INayaxConnectionRetestCredentialProvider.cs</c> -
    ///   documentation comments that point at it.</item>
    /// </list>
    /// </summary>
    private static readonly string[] FilesAllowedToReadTheGateFreeCredential =
    [
        "INayaxConnectionStore.cs",
        "EfNayaxConnectionStore.cs",
        "NayaxConnectionRetestCredentialProvider.cs",
        "INayaxConnectionRetestCredentialProvider.cs",
        "NayaxConnectionMigrator.cs",
        "NayaxConnection.cs",
        "NayaxConnectionSnapshot.cs",
        "NayaxConnectionCredential.cs",
    ];

    private static readonly string[] GuardedProjects =
    [
        "Inventory.Domain",
        "Inventory.Application",
        "Inventory.Infrastructure",
        "InventoryApi",
    ];

    /// <summary>
    /// A call of, or a reference to, the gate-free read. Whitespace around the dot is tolerated so a
    /// line-wrapped or reformatted call cannot slip past, and the match is deliberately not anchored
    /// to a receiver name: what is being banned is the member, however the expression reaches it.
    /// </summary>
    private static readonly Regex GateFreeCredentialRead = new(
        @"FindCredentialAsync\s*\(", RegexOptions.Compiled);

    /// <summary>
    /// No ordinary consumer <em>calls</em> the gate-free credential read, which is the boundary the
    /// type-level rules above cannot express (issue #681).
    ///
    /// <see cref="INayaxConnectionStore.FindCredentialAsync"/> returns a decrypted token without
    /// consulting <c>NayaxConnectionStatusGate</c>, and every Nayax consumer legitimately depends on
    /// the store that declares it - so "only the re-test may use it" was a comment rather than a
    /// rule. A use case that called it directly would authenticate to Nayax with credentials the gate
    /// refuses, including a connection an operator has been told needs attention, and no test would
    /// have failed.
    /// </summary>
    [Fact]
    public void No_ordinary_consumer_calls_the_gate_free_credential_read()
    {
        var offenders = GuardedProjects
            .SelectMany(project => SourceFilesOf(project).Select(path => (Project: project, Path: path)))
            .Where(file => !FilesAllowedToReadTheGateFreeCredential.Contains(
                Path.GetFileName(file.Path),
                StringComparer.Ordinal))
            .SelectMany(file => ReadsIn(file.Path).Select(read => $"{file.Project}: {read}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "INayaxConnectionStore.FindCredentialAsync returns a decrypted Nayax token without "
                + "applying NayaxConnectionStatusGate, so only the Owner-only re-test provider and "
                + "the human-invoked migrate-nayax-connection command may call it. An ordinary "
                + "consumer must resolve credentials through INayaxRequestCredentialProvider, which "
                + "applies the gate (AGENTS.md § Nayax integration, docs/architecture.md "
                + "§ Per-business Nayax credentials). Call site(s): "
                + string.Join("; ", offenders)
                + ". If a new caller is genuinely a recovery path, add its file to this test's "
                + "allow-list as a conscious, reviewed edit.");
    }

    /// <summary>
    /// Proves the rule above can see a call at all, rather than passing because the scan found
    /// nothing: the allowed files really do contain one.
    /// </summary>
    [Fact]
    public void The_source_scan_detects_the_calls_that_are_allowed()
    {
        var allowed = GuardedProjects
            .SelectMany(SourceFilesOf)
            .Where(path => FilesAllowedToReadTheGateFreeCredential.Contains(
                Path.GetFileName(path),
                StringComparer.Ordinal))
            .SelectMany(ReadsIn)
            .ToArray();

        Assert.Contains(allowed, read => read.StartsWith("NayaxConnectionRetestCredentialProvider.cs", StringComparison.Ordinal));
        Assert.Contains(allowed, read => read.StartsWith("NayaxConnectionMigrator.cs", StringComparison.Ordinal));
    }

    private static IEnumerable<string> SourceFilesOf(string project) =>
        Directory.EnumerateFiles(Path.Combine(BackendRoot, project), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static IEnumerable<string> ReadsIn(string path) =>
        File.ReadAllLines(path)
            .Select((line, index) => (Line: line, Number: index + 1))
            .Where(line => GateFreeCredentialRead.IsMatch(line.Line))
            .Select(line => $"{Path.GetFileName(path)}:{line.Number}");

    private static readonly string BackendRoot = FindBackendRoot();

    private static string FindBackendRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !Directory.Exists(Path.Combine(current.FullName, "Inventory.Domain")))
        {
            current = current.Parent;
        }

        return current?.FullName
            ?? throw new InvalidOperationException(
                "Could not locate the backend directory containing the Inventory.* projects.");
    }

    #endregion

    private static IReadOnlyCollection<string> Dependents(Assembly assembly, string dependency) =>
        Types.InAssembly(assembly)
            .That()
            .DoNotResideInNamespaceStartingWith(InstrumentationNamespacePrefix)
            .ShouldNot()
            .HaveDependencyOn(dependency)
            .GetResult()
            .FailingTypeNames?.ToArray() ?? [];

    private static void AssertOnlyAllowedDependents(string layer, Assembly assembly, string dependency)
    {
        var offenders = Dependents(assembly, dependency)
            .Where(name => !AllowedDependents.Contains(name, StringComparer.Ordinal))
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"'{dependency}' bypasses the Nayax connection status gate, so only the port, its "
                + $"implementation and the registration may name it. In {layer}, these types do: "
                + $"{string.Join(", ", offenders)}. Depend on INayaxRequestCredentialProvider "
                + "instead, or add the type to the allow-list in this test as a conscious, reviewed "
                + "edit (see docs/architecture.md § Per-business Nayax credentials).");
    }
}
