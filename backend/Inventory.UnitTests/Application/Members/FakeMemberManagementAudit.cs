using Inventory.Application.Members;

namespace InventoryApi.Tests.Application.Members;

/// <summary>
/// In-memory fake of the member management audit sink, so a test can assert both what was recorded
/// and - just as importantly - that nothing was recorded for a change that was refused or that
/// changed nothing.
/// </summary>
public sealed class FakeMemberManagementAudit : IMemberManagementAudit
{
    private readonly List<MemberChangeAuditEntry> _entries = [];

    /// <summary>Every event recorded, in order.</summary>
    public IReadOnlyList<MemberChangeAuditEntry> Entries
    {
        get
        {
            lock (_entries)
            {
                return [.. _entries];
            }
        }
    }

    public void Record(MemberChangeAuditEntry entry)
    {
        lock (_entries)
        {
            _entries.Add(entry);
        }
    }
}
