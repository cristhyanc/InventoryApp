namespace Inventory.Application.Expenses;

/// <summary>The supplier fields an operating expense response embeds, mirroring the Supplier entity's public shape.</summary>
public sealed record OperatingExpenseSupplier(int Id, string Name, string? ContactName, string? Phone, string? Email, string? Address);
