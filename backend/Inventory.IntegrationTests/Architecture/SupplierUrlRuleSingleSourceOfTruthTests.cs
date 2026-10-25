using System.Reflection;
using Inventory.Application.Suppliers;
using Inventory.Domain.Suppliers;
using Xunit;

namespace InventoryApi.Tests.Architecture;

/// <summary>
/// There is exactly one supplier-website rule in the backend, and it is issue #847's
/// <see cref="SupplierUrlPolicy"/> in the Application layer.
///
/// <para>This is a standing rule rather than one behaviour, and it is asserted structurally because
/// the damage a second copy does is not visible in either copy's own tests. Issue #848 wrote its own
/// <c>Inventory.Domain.Suppliers.SupplierUrlPolicy</c> while #847 was still open; when #847 merged
/// (PR #858) with the same rule in the Application layer, the two identically named policies made
/// <c>AppDbContext</c>'s single unqualified reference ambiguous and the build failed with
/// <c>CS0104</c>. The deeper problem is the one #848's fourth acceptance criterion names: issue #835
/// child C is to copy a preset into a business as an ordinary supplier row, so an address one rule
/// accepted and the other refused would be a copy that fails.</para>
///
/// <para>A duplicate is therefore refused here by name, in whichever layer it appears, and the
/// preset policy is asserted to hold no website rule of its own.</para>
/// </summary>
public class SupplierUrlRuleSingleSourceOfTruthTests
{
    private static readonly Assembly[] BackendAssemblies =
    [
        typeof(Inventory.Domain.AssemblyMarker).Assembly,
        typeof(Inventory.Application.AssemblyMarker).Assembly,
        typeof(Inventory.Infrastructure.AssemblyMarker).Assembly,
        typeof(Program).Assembly,
    ];

    [Fact]
    public void Exactly_one_supplier_url_policy_is_declared_across_the_backend()
    {
        var declared = BackendAssemblies
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type.Name == nameof(SupplierUrlPolicy))
            .Select(type => type.FullName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["Inventory.Application.Suppliers.SupplierUrlPolicy"], declared);
    }

    /// <summary>
    /// The preset's name rules stay in the Domain, where they belong - only the website is the
    /// Application layer's, because that is where the rule #847 shipped lives and where every
    /// supplier write path already runs it.
    /// </summary>
    [Fact]
    public void The_preset_policy_holds_the_name_rules_and_no_website_rule()
    {
        Assert.NotNull(typeof(SupplierPresetPolicy).GetMethod(nameof(SupplierPresetPolicy.NormaliseName)));
        Assert.NotNull(typeof(SupplierPresetPolicy).GetMethod(nameof(SupplierPresetPolicy.NormalisedName)));

        Assert.DoesNotContain(
            typeof(SupplierPresetPolicy).GetMethods(BindingFlags.Public | BindingFlags.Static),
            method => method.Name.Contains("Url", StringComparison.Ordinal));
    }
}
