// #4448: which objects of registered precompiled .app packages the app-group filter may hide.
// The CLI half is proven by tests/runner-extras (app-group-visibility-b's
// *_PrecompiledDepOfUnrelatedGroup_IsNotListed, app-group-visibility-install-dep and
// app-group-visibility-floor); this file pins the decision function.
using AlRunner;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public class PackageObjectVisibilityTests
{
    private static readonly Guid GroupA = new("4448aaaa-0000-4000-8000-00000000000a");
    private static readonly Guid GroupB = new("4448aaaa-0000-4000-8000-00000000000b");
    private static readonly Guid GroupC = new("4448aaaa-0000-4000-8000-00000000000c");
    private static readonly Guid PkgP1 = new("4448bbbb-0000-4000-8000-000000000001");
    private static readonly Guid PkgP0 = new("4448bbbb-0000-4000-8000-000000000000");
    private static readonly Guid PkgShared = new("4448bbbb-0000-4000-8000-0000000000cc");
    private static readonly Guid PkgUnclaimed = new("4448bbbb-0000-4000-8000-0000000000dd");
    private static readonly Guid MsApplication = new("4448cccc-0000-4000-8000-000000000001");
    private static readonly Guid MsBaseApp = new("4448cccc-0000-4000-8000-000000000002");
    private static readonly Guid MsSystemApp = new("4448cccc-0000-4000-8000-000000000003");
    private static readonly Guid MsTestLib = new("4448cccc-0000-4000-8000-000000000004");

    private static DependencyRef Dep(Guid id, string name = "", string publisher = "")
        => new(id, name, publisher, new Version(1, 0, 0, 0));

    private static RecordPatches.RegisteredPackage Pkg(Guid id, string name, string publisher,
        DependencyRef[] deps, params (string Kind, int Id)[] objects)
        => new(id, name, publisher, deps, objects);

    private static bool Hidden(RecordPatches.PackageVisibility model, Guid group, string kind, int id)
        => RecordPatches.IsHiddenFromAppGroup(kind, id,
            RecordPatches.VisibleAppClosure(group, model.Dependencies), model.Owners);

    [Fact]
    public void APackageOnlyOneGroupDeclares_IsHiddenFromTheOthers_AndVisibleToItsDeclarer()
    {
        var packages = new[] { Pkg(PkgP1, "P1", "Fixtures", Array.Empty<DependencyRef>(), ("Table", 61600), ("XmlPort", 61602)) };
        var groups = new Dictionary<Guid, DependencyRef[]>
        {
            [GroupA] = new[] { Dep(PkgP1) },
            [GroupB] = Array.Empty<DependencyRef>(),
        };

        var model = RecordPatches.BuildPackageVisibility(packages, groups);

        Assert.Equal(PkgP1, model.Owners[("table", 61600)]);
        Assert.True(Hidden(model, GroupB, "Table", 61600));
        Assert.True(Hidden(model, GroupB, "XMLport", 61602));
        Assert.False(Hidden(model, GroupA, "Table", 61600));
        Assert.False(Hidden(model, GroupA, "XMLport", 61602));
    }

    [Fact]
    public void APackageTwoGroupsDeclare_IsVisibleToBoth_AndStillHiddenFromAThird()
    {
        var packages = new[] { Pkg(PkgShared, "Shared", "Fixtures", Array.Empty<DependencyRef>(), ("Page", 70000)) };
        var groups = new Dictionary<Guid, DependencyRef[]>
        {
            [GroupA] = new[] { Dep(PkgShared) },
            [GroupB] = new[] { Dep(PkgShared) },
            [GroupC] = Array.Empty<DependencyRef>(),
        };

        var model = RecordPatches.BuildPackageVisibility(packages, groups);

        Assert.False(Hidden(model, GroupA, "Page", 70000));
        Assert.False(Hidden(model, GroupB, "Page", 70000));
        Assert.True(Hidden(model, GroupC, "Page", 70000));
    }

    [Fact]
    public void ThePackagesOwnManifestDependencies_AndASourceGroupsDependencies_AreFollowed()
    {
        // A -> P1 -> P0 (manifest); C -> A (source group). Both A and C reach P0; B reaches none.
        var packages = new[]
        {
            Pkg(PkgP1, "P1", "Fixtures", new[] { Dep(PkgP0) }, ("Table", 61600)),
            Pkg(PkgP0, "P0", "Fixtures", Array.Empty<DependencyRef>(), ("Table", 61500)),
        };
        var groups = new Dictionary<Guid, DependencyRef[]>
        {
            [GroupA] = new[] { Dep(PkgP1) },
            [GroupB] = Array.Empty<DependencyRef>(),
            [GroupC] = new[] { Dep(GroupA) },
        };

        var model = RecordPatches.BuildPackageVisibility(packages, groups);

        Assert.False(Hidden(model, GroupA, "Table", 61500));
        Assert.False(Hidden(model, GroupC, "Table", 61500));
        Assert.False(Hidden(model, GroupC, "Table", 61600));
        Assert.True(Hidden(model, GroupB, "Table", 61500));
        Assert.True(Hidden(model, GroupB, "Table", 61600));
    }

    [Fact]
    public void TheMicrosoftFloor_IsNeverHidden_EvenWhenAGroupClaimsItExplicitly()
    {
        // A declares a Microsoft test library that depends on System Application; B relies on the
        // floor alone. The floor apps stay unowned; the test library (not floor) is hideable.
        var packages = new[]
        {
            Pkg(MsApplication, "Application", "Microsoft", new[] { Dep(MsBaseApp), Dep(MsSystemApp) }),
            Pkg(MsBaseApp, "Base Application", "Microsoft", new[] { Dep(MsSystemApp) }, ("Table", 289)),
            Pkg(MsSystemApp, "System Application", "Microsoft", Array.Empty<DependencyRef>(), ("Table", 8700)),
            Pkg(MsTestLib, "System Application Test Library", "Microsoft", new[] { Dep(MsSystemApp) }, ("Codeunit", 130500)),
        };
        var groups = new Dictionary<Guid, DependencyRef[]>
        {
            [GroupA] = new[] { Dep(MsTestLib), Dep(MsBaseApp) },
            [GroupB] = Array.Empty<DependencyRef>(),
        };

        var model = RecordPatches.BuildPackageVisibility(packages, groups);

        Assert.False(model.Owners.ContainsKey(("table", 289)));
        Assert.False(model.Owners.ContainsKey(("table", 8700)));
        Assert.False(Hidden(model, GroupB, "Table", 289));
        Assert.False(Hidden(model, GroupB, "Table", 8700));
        Assert.True(Hidden(model, GroupB, "Codeunit", 130500));
        Assert.False(Hidden(model, GroupA, "Codeunit", 130500));
    }

    [Fact]
    public void TheFloor_IsRecognisedByName_WithoutTheApplicationUmbrella()
    {
        // No Microsoft/Application package registered: System Application is floor by its own name.
        var packages = new[]
        {
            Pkg(MsSystemApp, "System Application", "Microsoft", Array.Empty<DependencyRef>(), ("Table", 8700)),
        };
        var groups = new Dictionary<Guid, DependencyRef[]>
        {
            [GroupA] = new[] { Dep(MsSystemApp) },
            [GroupB] = Array.Empty<DependencyRef>(),
        };

        var model = RecordPatches.BuildPackageVisibility(packages, groups);

        Assert.False(Hidden(model, GroupB, "Table", 8700));
    }

    [Fact]
    public void APackageNoGroupReaches_KeepsNoOwner_SoItIsNeverHidden()
    {
        var packages = new[] { Pkg(PkgUnclaimed, "Loose", "Fixtures", Array.Empty<DependencyRef>(), ("Table", 61700)) };
        var groups = new Dictionary<Guid, DependencyRef[]>
        {
            [GroupA] = Array.Empty<DependencyRef>(),
            [GroupB] = Array.Empty<DependencyRef>(),
        };

        var model = RecordPatches.BuildPackageVisibility(packages, groups);

        Assert.Empty(model.Owners);
        Assert.False(Hidden(model, GroupA, "Table", 61700));
    }

    [Fact]
    public void AnIdTwoPackagesDeclare_HasNoSingleOwner_SoItIsNeverHidden()
    {
        var packages = new[]
        {
            Pkg(PkgP1, "P1", "Fixtures", Array.Empty<DependencyRef>(), ("Table", 61800), ("Table", 61801)),
            Pkg(PkgShared, "Shared", "Fixtures", Array.Empty<DependencyRef>(), ("Table", 61800)),
        };
        var groups = new Dictionary<Guid, DependencyRef[]>
        {
            [GroupA] = new[] { Dep(PkgP1), Dep(PkgShared) },
            [GroupB] = Array.Empty<DependencyRef>(),
        };

        var model = RecordPatches.BuildPackageVisibility(packages, groups);

        Assert.False(Hidden(model, GroupB, "Table", 61800));
        Assert.True(Hidden(model, GroupB, "Table", 61801));
    }

    [Fact]
    public void ADependencyWhoseIdMatchesNoPackage_ResolvesByNameAndPublisher()
    {
        // DependencyResolver.TryFind falls back to (name, publisher) when the id is not indexed;
        // the closure has to follow the same edge or the group would lose its own dependency.
        var packages = new[] { Pkg(PkgP1, "P1 Name", "Fixtures Pub", Array.Empty<DependencyRef>(), ("Table", 61600)) };
        var groups = new Dictionary<Guid, DependencyRef[]>
        {
            [GroupA] = new[] { Dep(Guid.NewGuid(), "P1 Name", "Fixtures Pub") },
            [GroupB] = new[] { Dep(PkgP1) },
            [GroupC] = Array.Empty<DependencyRef>(),
        };

        var model = RecordPatches.BuildPackageVisibility(packages, groups);

        Assert.False(Hidden(model, GroupA, "Table", 61600));
        Assert.True(Hidden(model, GroupC, "Table", 61600));
    }

    [Fact]
    public void TheWideningSkip_FiresOnlyWhenNoKeyCanBeHidden()
    {
        // The skip is exact only if "every owner is visible" implies "nothing is hidden". Checked
        // over every key of a real model, for a group that sees everything and one that does not.
        var packages = new[]
        {
            Pkg(PkgP1, "P1", "Fixtures", new[] { Dep(PkgP0) }, ("Table", 61600)),
            Pkg(PkgP0, "P0", "Fixtures", Array.Empty<DependencyRef>(), ("Table", 61500)),
            Pkg(PkgShared, "Shared", "Fixtures", Array.Empty<DependencyRef>(), ("Page", 70000)),
        };
        var groups = new Dictionary<Guid, DependencyRef[]>
        {
            [GroupA] = new[] { Dep(PkgP1), Dep(PkgShared) },
            [GroupB] = new[] { Dep(PkgShared) },
        };
        var model = RecordPatches.BuildPackageVisibility(packages, groups);

        var seesAll = RecordPatches.VisibleAppClosure(GroupA, model.Dependencies);
        Assert.True(RecordPatches.WideningCannotChangeAnAnswer(seesAll, model.OwnerApps));
        Assert.All(model.Owners.Keys, k => Assert.False(
            RecordPatches.IsHiddenFromAppGroup(k.Kind, k.Id, seesAll, model.Owners)));

        var seesSome = RecordPatches.VisibleAppClosure(GroupB, model.Dependencies);
        Assert.False(RecordPatches.WideningCannotChangeAnAnswer(seesSome, model.OwnerApps));
        Assert.Contains(model.Owners.Keys, k =>
            RecordPatches.IsHiddenFromAppGroup(k.Kind, k.Id, seesSome, model.Owners));
    }
}
