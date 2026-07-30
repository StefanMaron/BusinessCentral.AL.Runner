// SidecarClosureTests — the sidecar dependency closure a source dep must declare so BC's
// ReferenceManager can link cross-app type references in the dep's PUBLIC surface at
// downstream compile time (issue #1546).
//
// RED before the fix: the sidecar was written from `Dependencies.Where(d => !d.Optional)`,
// which drops the Microsoft platform apps (System Application, platform System, …) because
// they are synthesized as Optional implicit roots. A dependent then sees a parameter typed
// `Codeunit "Temp Blob"` (System Application) or `Enum "Copilot Capability"` (platform
// System) as `__MissingTypeSymbol__` (AL0133). BuildClosure must include those apps from
// the resolved closure AND from the platform apps vendored in the dep's own .alpackages.

using System;
using System.Linq;
using Xunit;
using AlRunner;

namespace AlRunner.Tests;

public sealed class SidecarClosureTests
{
    private static readonly Guid Self = Guid.Parse("dddddddd-0000-0000-0000-000000000001");
    private static readonly Guid SysAppId = Guid.Parse("63ca2fa4-4f03-4f2b-a480-172fef340d3f"); // System Application
    private static readonly Guid PlatSysId = Guid.Parse("8874ed3a-0643-4247-9ced-7a7002f7135d"); // platform System

    private static DepsSidecarWriter.DepEntry Dep(string name, Guid id, string ver = "28.2.0.0")
        => new("Microsoft", name, Version.Parse(ver), id);

    [Fact]
    public void BuildClosure_IncludesVendoredPlatformApps_NotInResolvedClosure()
    {
        // Resolved closure has only a vendored ISV dep — the platform apps entered the dep
        // compile via the raw .alpackages scan, NOT the resolved specs (implicit Optional roots).
        var resolved = new[] { Dep("Spare Brained Licensing", Guid.Parse("11111111-1111-1111-1111-111111111111")) };
        var vendored = new[] { Dep("System Application", SysAppId), Dep("System", PlatSysId) };

        var closure = DepsSidecarWriter.BuildClosure(resolved, vendored, Self);

        Assert.Contains(closure, d => d.AppId == SysAppId);   // carries "Temp Blob"
        Assert.Contains(closure, d => d.AppId == PlatSysId);  // carries "Copilot Capability"
        Assert.Contains(closure, d => d.Name == "Spare Brained Licensing");
        Assert.Equal(3, closure.Count);
    }

    [Fact]
    public void BuildClosure_DedupesByAppId_AndExcludesSelfAndEmpty()
    {
        // Same System Application appears in both inputs (different version copies); the dep's
        // own AppId and an unresolvable implicit root (Guid.Empty) must never be declared.
        var resolved = new[]
        {
            Dep("System Application", SysAppId, "28.2.50931.51111"),
            Dep("Pageworks", Self),                                   // self — must be dropped
            Dep("Application", Guid.Empty, "28.0.0.0"),               // unresolved implicit root — dropped
        };
        var vendored = new[] { Dep("System Application", SysAppId, "28.2.50931.52786") };

        var closure = DepsSidecarWriter.BuildClosure(resolved, vendored, Self);

        Assert.Single(closure);                       // only System Application survives
        Assert.Equal(SysAppId, closure[0].AppId);
        Assert.DoesNotContain(closure, d => d.AppId == Self);
        Assert.DoesNotContain(closure, d => d.AppId == Guid.Empty);
    }

    [Fact]
    public void BuildClosure_EmptyInputs_YieldsEmpty()
    {
        var closure = DepsSidecarWriter.BuildClosure(
            Array.Empty<DepsSidecarWriter.DepEntry>(),
            Array.Empty<DepsSidecarWriter.DepEntry>(),
            Self);
        Assert.Empty(closure);
    }
}
