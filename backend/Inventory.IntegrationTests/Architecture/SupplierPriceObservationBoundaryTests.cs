using System.Text.RegularExpressions;
using Xunit;

namespace InventoryApi.Tests.Architecture;

/// <summary>
/// Issue #925's last criterion, as a standing rule: <strong>no purchase, cost, inventory, GST
/// accounting or report code reads the observed supplier prices.</strong>
///
/// <para>An observed supplier price is purchasing guidance about a pack nobody has bought. A recorded
/// cost, an average unit cost, an inventory valuation, a GST figure and every report are built from
/// what the business actually paid and actually sold, and the one way this feature could do real harm
/// is by leaking into one of them - a reorder cost estimated from a shelf price, a valuation nudged
/// by a special at BIG W, a BAS figure that moved because a supermarket changed a label. None of
/// those would fail a test that only checked the comparison arithmetic, which is why the boundary is
/// pinned here instead.</para>
///
/// <para>The rule is checked against the source text, like <see cref="TimeAcquisitionTests"/> and
/// <see cref="ProviderLinkTableDisuseTests"/>, because what it forbids is a reference appearing at
/// all rather than a type-level dependency: the financial code and this feature live in the same
/// assemblies, so a compiler cannot express "these files may not know about those tables".</para>
///
/// <para>Two facts, deliberately. The first states the criterion over the financial and reporting
/// areas by name. The second is the stronger one: the only production files that mention these tables
/// or their policies at all are a declared list, so the next reader - the collector import of #937,
/// the summary endpoint of #930 - is added here on purpose rather than discovered later.</para>
/// </summary>
public class SupplierPriceObservationBoundaryTests
{
    /// <summary>
    /// Every name that belongs to issue #925's storage and comparison: the two tables, their
    /// entities, the <c>DbSet</c>s over them, the policies, the candidate and answer records, and the
    /// three columns a listing keeps about its own price checks.
    ///
    /// <para>Word-bounded, so a longer unrelated identifier does not match, and deliberately
    /// including the prose form of each name: a financial file that merely <em>mentions</em> observed
    /// supplier prices in a comment is a file whose author was thinking about reading them, and this
    /// boundary is cheap enough to hold at that line.</para>
    /// </summary>
    private static readonly Regex PriceObservationNames = new(
        @"\b(SupplierPriceObservations?|PriceCollectorRuns?|PriceCollectorRunStatus"
            + @"|SupplierPriceObservationPolicy|PriceCollectorRunPolicy|SupplierPriceFreshnessPolicy"
            + @"|SupplierListingPriceStatePolicy|CheapestUnitOfferPolicy|CheapestUnitOffers"
            + @"|SupplierUnitOffers?|SupplierUnitOfferPrice|SupplierOfferIneligibility"
            + @"|LatestObservationId|LastPriceChangeAtUtc|PriceCheckFreshnessHours)\b",
        RegexOptions.Compiled);

    /// <summary>
    /// The purchase, cost, inventory, GST-accounting and reporting areas of the production code, by
    /// path. Each one is asserted to exist, so a folder that is renamed or moved fails this test
    /// rather than silently emptying it.
    /// </summary>
    private static readonly string[] FinancialAndReportingAreas =
    [
        "Inventory.Domain/Purchases",
        "Inventory.Domain/Costing",
        "Inventory.Domain/Gst",
        "Inventory.Domain/Reporting",
        "Inventory.Domain/Stock",
        "Inventory.Domain/MachineStock",
        "Inventory.Domain/InventoryCounting",
        "Inventory.Domain/Expenses",
        "Inventory.Domain/FinancialConfiguration",
        "Inventory.Domain/SupplierOrders",
        "Inventory.Domain/NayaxFeeSettings",
        "Inventory.Application/Purchases",
        "Inventory.Application/Costing",
        "Inventory.Application/Gst",
        "Inventory.Application/Reporting",
        "Inventory.Application/Stock",
        "Inventory.Application/MachineStock",
        "Inventory.Application/InventoryCounting",
        "Inventory.Application/Expenses",
        "Inventory.Application/NayaxProcessingFees",
        "Inventory.Application/NayaxFeeSettings",
        "Inventory.Application/Commissions",
        "Inventory.Application/Dashboard",
        "Inventory.Application/Reorder",
        "Inventory.Application/SupplierOrders",
        "Inventory.Infrastructure/Reporting",
    ];

    /// <summary>
    /// The production files that may name issue #925's tables and policies: the Domain rules
    /// themselves, the two entities, the supplier listing and supplier master record that carry the
    /// columns, and the EF model that maps all of it.
    ///
    /// <para>Accepted <strong>by name</strong> rather than by exception, the way
    /// <c>BusinessOwnershipCoverageTests</c> accepts the two tenant-to-platform foreign keys: the next
    /// file to read an observed price is a reviewed decision - #937 writes them, #930 publishes a
    /// summary of them - and a file that starts reading them without arriving here is the change this
    /// assertion exists to make visible.</para>
    /// </summary>
    private static readonly string[] FilesThatMayNameThem =
    [
        // Issue #930's cheapest-supplier summary: the port and the use case that hand stored facts
        // to the comparison, the adapter that reads the listings and their latest observations, and
        // the HTTP boundary that publishes the answer. Each one is here on purpose - this list is
        // where a new reader of an observed price is declared, which is what makes the next one a
        // decision rather than a discovery.
        "Inventory.Application/PriceCatalogue/CheapestSupplierSummary.cs",
        "Inventory.Application/PriceCatalogue/ICheapestSupplierSummaryStore.cs",
        "Inventory.Domain/PriceCatalogue/CheapestUnitOfferPolicy.cs",
        "Inventory.Domain/PriceCatalogue/PriceCollectorRunPolicy.cs",
        "Inventory.Domain/PriceCatalogue/SupplierListingPriceStatePolicy.cs",
        "Inventory.Domain/PriceCatalogue/SupplierPriceFreshnessPolicy.cs",
        "Inventory.Domain/PriceCatalogue/SupplierPriceObservationPolicy.cs",
        "Inventory.Infrastructure/Data/AppDbContext.cs",
        "Inventory.Infrastructure/Models/PriceCollectorRun.cs",
        "Inventory.Infrastructure/Models/SupplierListing.cs",
        "Inventory.Infrastructure/Models/SupplierPreset.cs",
        "Inventory.Infrastructure/Models/SupplierPriceObservation.cs",
        "Inventory.Infrastructure/Persistence/EfCheapestSupplierSummaryStore.cs",
        "InventoryApi/Controllers/CheapestSuppliersController.cs",
        "InventoryApi/DTOs/CheapestSupplierDtos.cs",
    ];

    private static readonly string[] ProductionProjects =
        ["Inventory.Domain", "Inventory.Application", "Inventory.Infrastructure", "InventoryApi"];

    [Fact]
    public void No_financial_or_reporting_code_names_the_observed_supplier_prices()
    {
        var areas = FinancialAndReportingAreas
            .Select(area => Path.Combine(BackendRoot, area.Replace('/', Path.DirectorySeparatorChar)))
            .ToArray();

        Assert.All(
            areas,
            area => Assert.True(
                Directory.Exists(area),
                $"{area} is listed as a financial or reporting area and does not exist. Update the "
                    + "list in this test rather than leaving it matching nothing."));

        var offenders = areas
            .SelectMany(area => Directory.EnumerateFiles(area, "*.cs", SearchOption.AllDirectories))
            .Where(IsProductionSource)
            .SelectMany(MentionsIn)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "Observed supplier prices are purchasing guidance and must never reach a recorded cost, "
                + "an average unit cost, an inventory valuation, a GST accounting figure or a report "
                + "(issue #925). These financial or reporting files name issue #925's tables or "
                + $"policies: {string.Join("; ", offenders)}. See AGENTS.md § Observed supplier "
                + "prices and docs/architecture.md § Supplier price observations and comparison.");
    }

    [Fact]
    public void Only_the_price_catalogue_itself_and_the_ef_model_name_the_observed_supplier_prices()
    {
        var allowed = FilesThatMayNameThem
            .Select(file => file.Replace('/', Path.DirectorySeparatorChar))
            .ToHashSet(StringComparer.Ordinal);

        var naming = ProductionProjects
            .SelectMany(project => Directory.EnumerateFiles(
                Path.Combine(BackendRoot, project),
                "*.cs",
                SearchOption.AllDirectories))
            .Where(IsProductionSource)
            .Where(path => PriceObservationNames.IsMatch(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(BackendRoot, path))
            .Order(StringComparer.Ordinal)
            .ToArray();

        // Every allowlisted file really does name them, so the list cannot rot into a set of paths
        // that no longer exist, and every file that names them is allowlisted.
        Assert.Equal(allowed.Order(StringComparer.Ordinal).ToArray(), naming);
    }

    private static IEnumerable<string> MentionsIn(string path) =>
        File.ReadAllLines(path)
            .Select((line, index) => (Line: line, Number: index + 1))
            .Where(line => PriceObservationNames.IsMatch(line.Line))
            .Select(line =>
                $"{Path.GetRelativePath(BackendRoot, path)}:{line.Number} "
                    + $"({PriceObservationNames.Match(line.Line).Value})");

    /// <summary>
    /// Production source only: the build output is a copy, and the migrations are history - the one
    /// place these table names legitimately appear in statements, and statements that must not change.
    /// </summary>
    private static bool IsProductionSource(string path) =>
        !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !path.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

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
}
