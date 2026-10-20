using System.Text.Json.Serialization;

namespace Inventory.Infrastructure.Models;

public class OperatingExpense : IBusinessOwned
{
    /// <summary>
    /// The owning business (issue #64). Stamped from the caller's resolved business on insert
    /// and immutable afterwards; never read from API input, and never serialised out.
    /// </summary>
    [JsonIgnore]
    public int BusinessId { get; set; }

    public int Id { get; set; }
    public DateTime ExpenseDate { get; set; }
    public OperatingExpenseCategory Category { get; set; }
    public string Description { get; set; } = string.Empty;
    public decimal AmountExGst { get; set; }
    public decimal GstAmount { get; set; }
    public decimal TotalAmount { get; set; }
    public int? SupplierId { get; set; }
    public virtual Supplier? Supplier { get; set; }
    /// <summary>The provider's own site identifier, or <c>null</c> for a whole-business expense.</summary>
    public long? SiteId { get; set; }

    /// <summary>
    /// The machine this cost belongs to, as this business's own machine row (<c>Machines.Id</c>,
    /// issue #780), or <c>null</c> for a whole-business expense. It held the provider's machine
    /// number until <c>RepointOperatingExpenseMachines</c>; the pair <c>(BusinessId, MachineId)</c>
    /// is a foreign key on the <c>(BusinessId, Id)</c> key of <c>Machines</c>, so an expense cannot
    /// name another business's machine and deleting a machine an expense names fails loudly.
    /// </summary>
    public long? MachineId { get; set; }
    public string? AttachmentFileName { get; set; }
    [JsonIgnore]
    public string? AttachmentStoredFileName { get; set; }
    public string? AttachmentContentType { get; set; }
    public long? AttachmentFileSizeBytes { get; set; }
    public DateTime? ServicePeriodStart { get; set; }
    public DateTime? ServicePeriodEnd { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
