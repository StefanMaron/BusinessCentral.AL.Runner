using System;
using System.Collections.Generic;
using System.Linq;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Pins the two seams behind #2963 — the per-app package GUIDs the runner puts on BOTH sides
/// of a System Application module-ownership check, and the manifest-version split that fills
/// the Published Application row's four version columns.
///
/// WHY THESE ARE PINNED HERE AND NOT IN AL. The claim that "an app may register its own table
/// on the retention-policy allowed list" is plain BC behaviour and belongs upstream, where a
/// service tier adjudicates it. What cannot be reached from AL is the property these two
/// helpers have to hold for that to work at all: the package ids must DISCRIMINATE between
/// apps. Seed both sides with Guid.Empty and `ModuleOwnsTable`'s
/// `AllObj."App Runtime Package ID" &lt;&gt; PublishedApplication."Runtime Package ID"` compares
/// equal for every app/table pair — every check passes, and passes for the wrong reason. An AL
/// test asserting "my app can register my table" goes green either way, so it cannot see the
/// difference between a correct answer and a universally permissive one.
/// </summary>
public class AppPackageIdentityTests
{
    private static readonly Guid AppA = new("11111111-2222-3333-4444-555555555555");
    private static readonly Guid AppB = new("11111111-2222-3333-4444-555555555556"); // one bit apart

    [Fact]
    public void SameAppAlwaysGetsTheSameIds()
    {
        // Stability across calls, and therefore across processes: an install baseline captured
        // by one run is restored by the next, and the AllObj rows the next run rebuilds have to
        // still match the Published Application rows the snapshot carries.
        Assert.Equal(AppPackageIdentity.RuntimePackageIdFor(AppA), AppPackageIdentity.RuntimePackageIdFor(AppA));
        Assert.Equal(AppPackageIdentity.PackageIdFor(AppA), AppPackageIdentity.PackageIdFor(AppA));
    }

    [Fact]
    public void DifferentAppsGetDifferentIds()
    {
        // The whole point. Two apps whose ids differ by one bit must not collide.
        Assert.NotEqual(AppPackageIdentity.RuntimePackageIdFor(AppA), AppPackageIdentity.RuntimePackageIdFor(AppB));
        Assert.NotEqual(AppPackageIdentity.PackageIdFor(AppA), AppPackageIdentity.PackageIdFor(AppB));
    }

    [Fact]
    public void TheTwoColumnsAreNeverTheSameGuidForOneApp()
    {
        // Real BC assigns Package ID and Runtime Package ID independently. If the runner made
        // them equal, AL comparing one column against the other would silently succeed.
        Assert.NotEqual(AppPackageIdentity.PackageIdFor(AppA), AppPackageIdentity.RuntimePackageIdFor(AppA));
    }

    [Fact]
    public void AnUnknownOwnerStaysEmptyAndThereforeMatchesNothing()
    {
        // AllObj rows for an object whose owning app the runner could not determine keep
        // Guid.Empty. Deriving an id for Guid.Empty would hand those objects an owner, and the
        // ownership check would then pass for whichever app happened to derive the same value.
        Assert.Equal(Guid.Empty, AppPackageIdentity.RuntimePackageIdFor(Guid.Empty));
        Assert.Equal(Guid.Empty, AppPackageIdentity.PackageIdFor(Guid.Empty));
        Assert.NotEqual(Guid.Empty, AppPackageIdentity.RuntimePackageIdFor(AppA));
    }

    [Fact]
    public void ManyAppsDoNotCollide()
    {
        // A closure of a few dozen apps is ordinary (Base Application alone ships alongside
        // System Application, Business Foundation and the test libraries). A collision would
        // make one app own another's tables.
        var ids = Enumerable.Range(0, 500)
            .Select(i => AppPackageIdentity.RuntimePackageIdFor(
                new Guid(i, 0, 0, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 })))
            .ToList();
        Assert.Equal(ids.Count, new HashSet<Guid>(ids).Count);
    }

    [Theory]
    [InlineData("28.1.49838.54308", 28, 1, 49838, 54308)]
    [InlineData("1.0.0.0", 1, 0, 0, 0)]
    // BC filters Published Application on all four columns, so a manifest that states fewer
    // parts must leave the rest at 0 rather than inferring them from the parts it has.
    [InlineData("2.5", 2, 5, 0, 0)]
    [InlineData("", 0, 0, 0, 0)]
    [InlineData(null, 0, 0, 0, 0)]
    // A non-numeric part is 0, not an exception: a bad manifest must not abort the run.
    [InlineData("1.x.3", 1, 0, 3, 0)]
    public void SplitManifestVersion_FillsTheFourColumns(string? version, int major, int minor, int build, int revision)
    {
        var actual = RecordPatches.SplitManifestVersion(version);
        Assert.Equal((major, minor, build, revision), actual);
    }
}
