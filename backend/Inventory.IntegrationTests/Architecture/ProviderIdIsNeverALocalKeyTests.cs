using System.Text.RegularExpressions;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace InventoryApi.Tests.Architecture;

/// <summary>
/// Issue #735 (parent #329 decision A4) ended the coincidence this application was built on: a new
/// product's and a new category's <c>Id</c> is now the database's own, and a provider's id lives only
/// in <c>Product.ExternalProductId</c>/<c>Category.ExternalCategoryId</c> (issue #759). From here a
/// local key and a Nayax id that are the same number are two unrelated facts.
///
/// <para>That makes a whole class of code silently wrong rather than visibly broken: comparing a
/// <c>NayaxProductID</c> or a <c>ProductGroupID</c> with <c>Product.Id</c>/<c>Category.Id</c>, or
/// reading the stored <c>NayaxSales.NayaxProductId</c> as a local product key, now resolves to
/// whichever row happens to hold that number - a different product's inventory, cost and reported
/// revenue. Nothing in the type system says no, because both sides are a <c>long</c>. This test is
/// what says no.</para>
///
/// <para>Two of the three rules are checked against the source text, like
/// <see cref="ProviderLinkTableDisuseTests"/> and <see cref="TimeAcquisitionTests"/>, because what
/// they forbid is an assignment and a comparison rather than a type-level dependency. They are
/// deliberately narrow and literal: they catch the shapes this slice removed and the shapes a future
/// change would most plausibly reintroduce, and they are pinned by their own samples below so the
/// guard cannot quietly stop matching anything. The third rule reads the EF model, because "the
/// database assigns the key" is a property of the model and the schema, not of any one call site.</para>
/// </summary>
public class ProviderIdIsNeverALocalKeyTests
{
    private static readonly string[] ProductionProjects =
        ["Inventory.Domain", "Inventory.Application", "Inventory.Infrastructure", "InventoryApi"];

    /// <summary>
    /// Writing a <c>Product</c>'s or <c>Category</c>'s own key, in an object initializer
    /// (<c>new Product { Id = … }</c>) or as a property assignment (<c>product.Id = …</c>).
    ///
    /// <para>Nothing in production code may do either any more, whatever the value comes from: the
    /// database assigns it. A rule about the assignment rather than about its right-hand side is what
    /// makes this checkable at all - a provider id arrives as an ordinary <c>long</c>, so the only
    /// honest static answer is that this key is never written by hand.</para>
    ///
    /// <para><c>ExternalProductId</c>, <c>ExternalCategoryId</c>, <c>CategoryId</c> and the other
    /// <c>…Id</c> properties do not match, because <c>\b</c> finds no word boundary inside an
    /// identifier.</para>
    /// </summary>
    private static readonly Regex LocalKeyWrite = new(
        @"new\s+(?:Product|Category)\s*\{[^{}]*?\bId\s*=(?!=)"
            + @"|\b[A-Za-z_]*(?:[Pp]roduct|[Cc]ategory)[A-Za-z_]*\.Id\s*=(?!=)",
        RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>
    /// Reading a provider's id as a local key: comparing a Nayax product or group id with a row's
    /// <c>.Id</c>, in either direction, or assigning one straight into a local
    /// <c>ProductId</c>/<c>CategoryId</c>.
    ///
    /// <para>The assignment half deliberately matches only a direct read - <c>ProductId =
    /// sale.NayaxProductId</c> - and not a resolution, because <c>ProductId =
    /// LocalProductId(sale.NayaxProductId, …)</c> is exactly what the writers are supposed to do
    /// (issues #745 and #758): the provider id is resolved through
    /// <c>INayaxProductLinkStore</c>/<c>Product.ExternalProductId</c> and the <em>answer</em> is
    /// stored. <c>ExternalProductId = NayaxExternalId.From(…)</c> is excluded for the same reason:
    /// that column is where a provider id belongs.</para>
    /// </summary>
    private static readonly Regex ProviderIdReadAsLocalKey = new(
        @"\.Id\s*==\s*[A-Za-z0-9_.!]*(?:[Nn]ayax|ProductGroupI[Dd]|[Ee]xternal)[A-Za-z0-9_.!]*"
            + @"|[A-Za-z0-9_.!]*(?:[Nn]ayax|ProductGroupI[Dd]|[Ee]xternal)[A-Za-z0-9_.!]*\s*==\s*[A-Za-z0-9_.!]*\.Id\b"
            + @"|(?<!Nayax)(?<!External)(?:Product|Category)Id\s*=\s*[A-Za-z0-9_.!]*\b(?:Nayax[A-Za-z0-9_]*Id|ProductGroupI[Dd])\b[A-Za-z0-9_.!]*\s*[;,)]",
        RegexOptions.Compiled);

    [Fact]
    public void No_production_code_writes_a_product_or_category_id()
    {
        var offenders = OffendersOf(LocalKeyWrite);

        Assert.True(
            offenders.Length == 0,
            "A product's and a category's Id is assigned by the database since issue #735 (A4), and no "
                + "production code may write one - least of all from a provider value. Create the row "
                + "without an Id and record the provider's id in Product.ExternalProductId / "
                + "Category.ExternalCategoryId instead, at: "
                + string.Join("; ", offenders)
                + ". See docs/architecture.md § Provider identity on the local row.");
    }

    [Fact]
    public void No_production_code_reads_a_provider_id_as_a_local_product_or_category_key()
    {
        var offenders = OffendersOf(ProviderIdReadAsLocalKey);

        Assert.True(
            offenders.Length == 0,
            "A Nayax NayaxProductID or ProductGroupID must never be compared with Product.Id or "
                + "Category.Id, and the stored NayaxSales.NayaxProductId is a provider identity rather "
                + "than a local product key (issues #735 and #758). Resolve the provider id through "
                + "INayaxProductLinkStore / Product.ExternalProductId, or read the sale's own ProductId, "
                + "at: "
                + string.Join("; ", offenders)
                + ". See docs/architecture.md § Provider identity on the local row and § The sale's own "
                + "product.");
    }

    /// <summary>
    /// The model half of the same rule, and the reason the import can stop assigning a key at all:
    /// both tables generate their own. It is asserted against the model rather than the import,
    /// because a model that stopped generating the key would make the import write <c>0</c> for
    /// every row rather than fail.
    /// </summary>
    [Fact]
    public void Product_and_category_keys_are_generated_by_the_database()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        using var db = TestAppDbContext.Denied(options);

        foreach (var entity in new[] { typeof(Product), typeof(Category) })
        {
            var key = db.Model.FindEntityType(entity)!.FindProperty("Id")!;
            Assert.Equal(ValueGenerated.OnAdd, key.ValueGenerated);
        }
    }

    /// <summary>
    /// The guards' own test. A source-text rule that matches nothing is indistinguishable from a rule
    /// that is satisfied, so each one is pinned against the exact shapes this slice removed - and
    /// against the shapes that must stay allowed, which is what keeps the rules usable rather than
    /// merely strict.
    /// </summary>
    [Theory]
    [InlineData("var created = new Category { Id = category.Id, Name = category.Name };", true)]
    [InlineData("product = new Product { Id = entry.Id, RestockTo = 0 };", true)]
    [InlineData("product.Id = entry.NayaxProductId;", true)]
    [InlineData("localCategory.Id = groupId;", true)]
    [InlineData("var created = new Category { ExternalCategoryId = externalId, Name = name };", false)]
    [InlineData("product.ExternalProductId = NayaxExternalId.From(entry.NayaxProductId);", false)]
    [InlineData("product.CategoryId = localCategoryId;", false)]
    [InlineData("link.ProductId = product.Id;", false)]
    public void The_local_key_write_rule_matches_exactly_the_forbidden_shapes(string line, bool forbidden) =>
        Assert.Equal(forbidden, LocalKeyWrite.IsMatch(line));

    [Theory]
    [InlineData("localCategories.Any(local => local.Id == category.NayaxProductGroupId)", true)]
    [InlineData("products.Where(p => p.Id == sale.NayaxProductId)", true)]
    [InlineData("candidates.Single(c => entry.NayaxProductId == c.Id)", true)]
    [InlineData("entity.ProductId = sale.NayaxProductId;", true)]
    [InlineData("product.CategoryId = entry.NayaxProductGroupId;", true)]
    [InlineData("entity.ProductId = LocalProductId(sale.NayaxProductId, localProductIdByNayaxId);", false)]
    [InlineData("entity.NayaxProductId = sale.NayaxProductId;", false)]
    [InlineData("product.ExternalProductId = NayaxExternalId.From(entry.NayaxProductId);", false)]
    [InlineData("products.Where(p => p.Id == localProductId)", false)]
    [InlineData("product.CategoryId = LocalCategoryId(entry.NayaxProductGroupId, byExternalId);", false)]
    public void The_provider_id_read_rule_matches_exactly_the_forbidden_shapes(string line, bool forbidden) =>
        Assert.Equal(forbidden, ProviderIdReadAsLocalKey.IsMatch(line));

    private static string[] OffendersOf(Regex rule) =>
        ProductionProjects
            .SelectMany(project => SourceFilesOf(project).Select(path => (Project: project, Path: path)))
            .SelectMany(file => MatchesIn(file.Path, rule).Select(match => $"{file.Project}: {match}"))
            .OrderBy(offender => offender, StringComparer.Ordinal)
            .ToArray();

    private static IEnumerable<string> SourceFilesOf(string project) =>
        Directory.EnumerateFiles(Path.Combine(BackendRoot, project), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            // A migration is a historical statement about a schema that already exists, and the two
            // that backfilled the provider ids had to name both sides of the coincidence to copy it.
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    /// <summary>
    /// Matched line by line, except for the object-initializer shape, which spans lines: that one is
    /// matched over the whole file and reported at the line its <c>new</c> starts on.
    /// </summary>
    private static IEnumerable<string> MatchesIn(string path, Regex rule)
    {
        var text = File.ReadAllText(path);
        foreach (var match in rule.Matches(text).Cast<Match>())
        {
            var line = text.Take(match.Index).Count(character => character == '\n') + 1;
            var first = match.Value.Split('\n')[0].Trim();
            yield return $"{Path.GetFileName(path)}:{line} ({first})";
        }
    }

    private static readonly string BackendRoot = FindBackendRoot();

    private static string FindBackendRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !Directory.Exists(Path.Combine(current.FullName, "Inventory.Domain")))
        {
            current = current.Parent;
        }

        return current?.FullName
            ?? throw new InvalidOperationException("Could not locate the backend directory containing the Inventory.* projects.");
    }
}
