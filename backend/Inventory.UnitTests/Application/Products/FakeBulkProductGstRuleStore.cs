#nullable enable

using Inventory.Application.Products;
using Inventory.Domain.Gst;

namespace InventoryApi.Tests.Application.Products;

/// <summary>
/// An in-memory stand-in for the bulk product GST rule port: a set of stored product rules, the
/// eligibility rule applied to them, and a record of what the use case asked for.
///
/// It applies <see cref="BulkProductGstRulePolicy"/> itself rather than a copy of it, including the
/// expected-count comparison, because the real adapter does exactly that inside its write
/// transaction - and a fake that compared the counts differently would let a use-case test pass
/// while the shipped behaviour differed.
/// </summary>
public sealed class FakeBulkProductGstRuleStore : IBulkProductGstRuleStore
{
    private readonly List<GstClassification> _rules;

    public FakeBulkProductGstRuleStore(params GstClassification[] storedRules) =>
        _rules = [.. storedRules];

    /// <summary>Every stored rule, in the order the products were given to the constructor.</summary>
    public IReadOnlyList<GstClassification> StoredRules => _rules;

    /// <summary>The target rule of the last write attempt, or <see langword="null"/> if none.</summary>
    public GstClassification? LastRequestedRule { get; private set; }

    /// <summary>The eligible count the last write attempt presented, or <see langword="null"/>.</summary>
    public int? LastExpectedEligibleCount { get; private set; }

    /// <summary>
    /// What the next read answers in place of the stored set, so a test can stage a change that
    /// commits between the preview and the apply.
    /// </summary>
    public Action? BeforeWrite { get; set; }

    public int CountCallCount { get; private set; }

    public Task<int> CountProductsWithNoRuleAsync(CancellationToken cancellationToken)
    {
        CountCallCount++;
        return Task.FromResult(_rules.Count(BulkProductGstRulePolicy.IsEligible));
    }

    public Task<int> SetRuleOnProductsWithNoRuleAsync(
        GstClassification rule, int expectedEligibleCount, CancellationToken cancellationToken)
    {
        LastRequestedRule = rule;
        LastExpectedEligibleCount = expectedEligibleCount;

        BeforeWrite?.Invoke();

        var eligible = _rules.Count(BulkProductGstRulePolicy.IsEligible);
        BulkProductGstRulePolicy.RequireUnchangedEligibleCount(expectedEligibleCount, eligible);

        var changed = 0;
        for (var index = 0; index < _rules.Count; index++)
        {
            if (!BulkProductGstRulePolicy.IsEligible(_rules[index])) continue;

            _rules[index] = rule;
            changed++;
        }

        return Task.FromResult(changed);
    }

    /// <summary>Sets one stored product's rule, as another caller committing in between would.</summary>
    public void SetStoredRule(int index, GstClassification rule) => _rules[index] = rule;

    /// <summary>Adds a product with no rule, as a concurrent create would.</summary>
    public void AddProductWithNoRule() => _rules.Add(GstRules.None);
}
