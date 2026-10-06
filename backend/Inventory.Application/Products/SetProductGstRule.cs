using Inventory.Application.Gst;
using Inventory.Domain.Gst;

namespace Inventory.Application.Products;

/// <summary>
/// Configures one product's GST rule (issue #430).
///
/// It writes the rule and nothing else. Setting a rule is configuration, not classification: no
/// existing purchase line or charge is reclassified by it, and historical classification stays the
/// explicit Admin Preview/Apply maintenance workflow (issue #433).
///
/// An unsupported rule is refused before the product is even looked up, so an undefined value can
/// never be stored - whatever product it names, and whether that product exists or not. It has to
/// be checked here rather than left to the enum type: the value arrives from a deserialized JSON
/// body, where any integer binds to a <see cref="GstClassification"/>.
/// </summary>
public sealed class SetProductGstRule
{
    private readonly IProductStore _store;

    public SetProductGstRule(IProductStore store)
    {
        _store = store;
    }

    public async Task<GstRuleUpdateResult> Handle(
        long id,
        GstClassification rule,
        CancellationToken cancellationToken)
    {
        if (!GstRules.IsSupportedRule(rule))
            return GstRuleUpdateResult.Invalid(GstRules.UnsupportedRuleMessage);

        return await _store.SetGstRuleAsync(id, rule, cancellationToken)
            ? GstRuleUpdateResult.Success()
            : GstRuleUpdateResult.NotFound();
    }
}
