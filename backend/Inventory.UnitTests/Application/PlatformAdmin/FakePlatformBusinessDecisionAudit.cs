using Inventory.Application.PlatformAdmin;

namespace InventoryApi.Tests.Application.PlatformAdmin;

/// <summary>
/// Collects the platform business-decision audit events of issue #544, so a test can assert both
/// halves: that a committed decision is recorded once with the actor, the business and the status
/// transition, and that a refused decision records nothing at all.
/// </summary>
public sealed class FakePlatformBusinessDecisionAudit : IPlatformBusinessDecisionAudit
{
    private readonly List<PlatformBusinessDecisionAuditEntry> _entries = [];

    public IReadOnlyList<PlatformBusinessDecisionAuditEntry> Entries => _entries;

    public void Record(PlatformBusinessDecisionAuditEntry entry) => _entries.Add(entry);
}
