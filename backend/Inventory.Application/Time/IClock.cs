namespace Inventory.Application.Time;

/// <summary>
/// Narrow port for the current UTC instant, so use cases stay deterministic and testable.
/// </summary>
public interface IClock
{
    DateTime UtcNow { get; }
}
