using Inventory.Application.Members;

namespace InventoryApi.Tests.Application.Members;

/// <summary>
/// In-memory fake of the invite audit sink, so a test can assert both what was recorded and - just
/// as importantly - that nothing was recorded for a change that was refused or that changed
/// nothing.
/// </summary>
public sealed class FakeMemberInviteAudit : IMemberInviteAudit
{
    private readonly List<MemberInviteAuditEntry> _entries = [];

    /// <summary>Every event recorded, in order.</summary>
    public IReadOnlyList<MemberInviteAuditEntry> Entries
    {
        get
        {
            lock (_entries)
            {
                return [.. _entries];
            }
        }
    }

    public void Record(MemberInviteAuditEntry entry)
    {
        lock (_entries)
        {
            _entries.Add(entry);
        }
    }
}
