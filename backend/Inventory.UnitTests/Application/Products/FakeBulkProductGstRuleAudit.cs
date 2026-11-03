#nullable enable

using Inventory.Application.Products;

namespace InventoryApi.Tests.Application.Products;

/// <summary>
/// Collects the bulk product GST rule audit events a use-case test produced, so the test can show
/// what is recorded - and, just as importantly, that nothing is recorded for a refused attempt.
/// </summary>
public sealed class FakeBulkProductGstRuleAudit : IBulkProductGstRuleAudit
{
    private readonly List<BulkProductGstRuleAuditEntry> _entries = [];

    public IReadOnlyList<BulkProductGstRuleAuditEntry> Entries => _entries;

    public void Record(BulkProductGstRuleAuditEntry entry) => _entries.Add(entry);
}
