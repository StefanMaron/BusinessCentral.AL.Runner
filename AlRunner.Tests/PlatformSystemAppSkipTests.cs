// PlatformSystemAppSkipTests — the platform symbol app "System" (Microsoft,
// AppId 8874ed3a-0643-4247-9ced-7a7002f7135d, objects 2000000000..2000001000)
// ships symbol-only AL whose procedure bodies are external/native. Its Tier-3
// source-compile ALWAYS fails (CS0103/CS1061 on `_Internal` platform methods)
// and the dependency loader then falls back to service-tier DLL dispatch anyway.
// DependencyLoader must therefore skip the doomed Emit+Roslyn pass up front —
// same observable outcome, without paying a multi-second failed compile on
// every run. These tests lock the predicate that gates the skip.

using Xunit;
using AlRunner.Infrastructure;

namespace AlRunner.Tests;

public sealed class PlatformSystemAppSkipTests
{
    private static readonly Guid SystemAppId = Guid.Parse("8874ed3a-0643-4247-9ced-7a7002f7135d");

    [Fact]
    public void SystemApp_ByWellKnownAppId_IsSkipped()
    {
        // Positive: the canonical platform System app identity (name may drift, id may not).
        Assert.True(ProvisioningCheck.IsPlatformSymbolOnlySystemApp(
            SystemAppId, "Microsoft", "System"));
    }

    [Fact]
    public void SystemApp_ByPublisherAndName_IsSkipped_CaseInsensitive()
    {
        // Positive: matched by publisher+name even with an unknown/empty AppId.
        Assert.True(ProvisioningCheck.IsPlatformSymbolOnlySystemApp(
            Guid.Empty, "microsoft", "SYSTEM"));
    }

    [Fact]
    public void SystemApplication_IsNotTheSystemApp()
    {
        // Negative: "System Application" is a DIFFERENT app (R2R runtime package,
        // handled by IsKnownPlatformRuntimeApp) and must NOT be swallowed here.
        Assert.False(ProvisioningCheck.IsPlatformSymbolOnlySystemApp(
            Guid.Parse("63ca2fa4-4f03-4f2b-a480-172fef340d3f"), "Microsoft", "System Application"));
    }

    [Fact]
    public void ThirdPartySystemNamedApp_IsNotSkipped()
    {
        // Negative: an ISV app that happens to be called "System" must still
        // source-compile (and fail LOUD if broken) — only Microsoft's platform
        // symbols get the skip.
        Assert.False(ProvisioningCheck.IsPlatformSymbolOnlySystemApp(
            Guid.NewGuid(), "Contoso", "System"));
    }
}
