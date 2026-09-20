namespace Inventory.Application.NayaxFeeSettings;

/// <summary>
/// Narrow port for the current UTC instant, so use cases stay deterministic and testable.
/// </summary>
public interface IClock
{
    DateTime UtcNow { get; }
}
