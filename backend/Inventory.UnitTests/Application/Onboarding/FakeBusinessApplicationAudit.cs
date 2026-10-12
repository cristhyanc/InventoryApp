using Inventory.Application.Onboarding;

namespace InventoryApi.Tests.Application.Onboarding;

/// <summary>
/// Collects the business-application audit events of issue #541, so a test can assert both halves:
/// that a committed application is recorded once with the actor and the new business id, and that a
/// refused submission or a resubmission records nothing at all.
/// </summary>
public sealed class FakeBusinessApplicationAudit : IBusinessApplicationAudit
{
    private readonly List<BusinessApplicationAuditEntry> _entries = [];

    public IReadOnlyList<BusinessApplicationAuditEntry> Entries => _entries;

    public void Record(BusinessApplicationAuditEntry entry) => _entries.Add(entry);
}
