using Inventory.Application.Gst;
using Inventory.Domain.Gst;

namespace Inventory.Application.Suppliers;

/// <summary>
/// Configures one supplier's GST defaults (issue #430).
///
/// It writes the three defaults and nothing else. A default is configuration, not classification:
/// no existing purchase line or charge is reclassified by it, and it only ever applies where no
/// product rule exists, which the historical Preview/Apply maintenance workflow (issue #433)
/// decides.
///
/// An unsupported value in any of the three is refused before the supplier is even looked up, so a
/// rule no policy describes can never be stored. It has to be checked here rather than left to the
/// enum type: the values arrive from a deserialized JSON body, where any integer binds to a
/// <see cref="GstClassification"/>.
/// </summary>
public sealed class SetSupplierGstDefaults
{
    private readonly ISupplierStore _store;

    public SetSupplierGstDefaults(ISupplierStore store)
    {
        _store = store;
    }

    public async Task<GstRuleUpdateResult> Handle(
        int id,
        SupplierGstDefaults defaults,
        CancellationToken cancellationToken)
    {
        if (defaults.HasUnsupportedRule)
            return GstRuleUpdateResult.Invalid(GstRules.UnsupportedRuleMessage);

        return await _store.SetGstDefaultsAsync(id, defaults, cancellationToken)
            ? GstRuleUpdateResult.Success()
            : GstRuleUpdateResult.NotFound();
    }
}
