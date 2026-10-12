using Inventory.Application.Onboarding;

namespace InventoryApi.Tests.Application.Onboarding;

/// <summary>
/// The self-service onboarding flag of issue #541, as a test decides it, so a test never has to
/// build an <c>IConfiguration</c> to say "onboarding is on here".
/// </summary>
public sealed class FakeSelfServiceOnboardingFlag : ISelfServiceOnboardingFlag
{
    private FakeSelfServiceOnboardingFlag(bool isEnabled) => IsEnabled = isEnabled;

    public static FakeSelfServiceOnboardingFlag Enabled { get; } = new(isEnabled: true);

    /// <summary>The shipped state: no deployment has switched onboarding on.</summary>
    public static FakeSelfServiceOnboardingFlag Disabled { get; } = new(isEnabled: false);

    public bool IsEnabled { get; }
}
