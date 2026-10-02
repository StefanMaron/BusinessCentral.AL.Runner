// DotNetRefPackDiagnosisTests — #5134.
//
// RUNNER-MECHANISM test: the claim is about which dotnet install the RUNNER reads reference
// packs from and what it tells the user when they are absent. Nothing here is a statement about
// Business Central, and the corpus cannot express it — the corpus runs on a service tier that has
// no notion of an SDK `packs/` directory — so there is no corpus half.
//
// CLAIM: a runtime-only dotnet install (no packs/) is named as the cause, with the root searched
// and the fix, rather than leaving every `DotNet` alias to fail AL0185 object by object. Each test
// builds a throwaway dotnet root, so the answer does not depend on what this machine has.
//
// TRAP: the diagnosis and the enumeration must read the same directories, or a warning could
// fire on a box that binds fine (or stay silent on one that does not). The "control" tests pin that.

using AlRunner;
using Xunit;

namespace AlRunner.Tests;

public sealed class DotNetRefPackDiagnosisTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "alr-refpack-" + Guid.NewGuid().ToString("N")[..8]);

    public DotNetRefPackDiagnosisTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // A runtime directory shaped like a real one: <root>/shared/Microsoft.NETCore.App/<version>/
    private string RuntimeDir(string version = "8.0.30")
    {
        var d = Path.Combine(_root, "shared", "Microsoft.NETCore.App", version) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(d);
        return d;
    }

    private string AddPack(string pack, string version, string tfm)
    {
        var d = Path.Combine(_root, "packs", pack, version, "ref", tfm);
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void RuntimeOnlyInstall_IsNamedWithTheRootSearchedAndTheFix()
    {
        var gap = BcCompiler.DiagnoseDotNetRefPacks(RuntimeDir(), null);

        Assert.NotNull(gap);
        Assert.True(gap!.AliasesCannotBind);
        Assert.Contains(_root, gap.Warning);                       // the root that was searched
        Assert.Contains("Microsoft.NETCore.App.Ref for .NET 8", gap.Warning);
        Assert.Contains("NETStandard.Library.Ref", gap.Warning);
        Assert.Contains("AL0185", gap.Warning);                    // what the user will see otherwise
        Assert.Contains("install the .NET SDK", gap.Warning);      // the fix
    }

    [Fact]
    public void CompleteInstall_ReportsNothing_AndTheEnumerationYieldsBothPacks()
    {
        var core = AddPack("Microsoft.NETCore.App.Ref", "8.0.30", "net8.0");
        var ns = AddPack("NETStandard.Library.Ref", "2.1.0", "netstandard2.1");

        Assert.Null(BcCompiler.DiagnoseDotNetRefPacks(RuntimeDir(), null));
        // Control: the same root the enumeration reads really does supply the directories, so a
        // null above means "usable", not "the diagnosis looked somewhere else".
        var probed = BcCompiler.EnumerateDotNetRefAssemblyDirsUnder(RuntimeDir(), null).ToList();
        Assert.Equal(new[] { core, ns }, probed);
    }

    [Fact]
    public void RuntimeOnlyInstall_ProbesNothing_SoTheWarningDescribesWhatBCActuallyGets()
    {
        Assert.Empty(BcCompiler.EnumerateDotNetRefAssemblyDirsUnder(RuntimeDir(), null));
    }

    [Fact]
    public void OnlyANetStandardPackMissing_NamesThatPackAlone()
    {
        AddPack("Microsoft.NETCore.App.Ref", "8.0.30", "net8.0");

        var gap = BcCompiler.DiagnoseDotNetRefPacks(RuntimeDir(), null);

        Assert.NotNull(gap);
        Assert.True(gap!.AliasesCannotBind);
        Assert.Contains("NETStandard.Library.Ref is not installed", gap.Warning);
        Assert.DoesNotContain("Microsoft.NETCore.App.Ref for .NET 8 is not installed", gap.Warning);
    }

    [Fact]
    public void OnlyTheCorePackMissing_NamesThatPackAlone()
    {
        AddPack("NETStandard.Library.Ref", "2.1.0", "netstandard2.1");

        var gap = BcCompiler.DiagnoseDotNetRefPacks(RuntimeDir(), null);

        Assert.NotNull(gap);
        Assert.Contains("Microsoft.NETCore.App.Ref for .NET 8 is not installed", gap!.Warning);
        Assert.DoesNotContain("NETStandard.Library.Ref is not installed", gap.Warning);
    }

    /// <summary>A pack folder with no ref/&lt;tfm&gt; yields nothing to probe, so it is missing.</summary>
    [Fact]
    public void AnEmptyPackFolder_CountsAsMissing()
    {
        Directory.CreateDirectory(Path.Combine(_root, "packs", "Microsoft.NETCore.App.Ref", "8.0.30"));
        AddPack("NETStandard.Library.Ref", "2.1.0", "netstandard2.1");

        var gap = BcCompiler.DiagnoseDotNetRefPacks(RuntimeDir(), null);

        Assert.NotNull(gap);
        Assert.True(gap!.AliasesCannotBind);
        Assert.Contains("Microsoft.NETCore.App.Ref for .NET 8 is not installed", gap.Warning);
    }

    /// <summary>The same, one level down: a <c>ref/</c> folder holding no target-framework folder.</summary>
    [Fact]
    public void ARefFolderWithNoTargetFramework_CountsAsMissing()
    {
        Directory.CreateDirectory(Path.Combine(_root, "packs", "Microsoft.NETCore.App.Ref", "8.0.30", "ref"));
        AddPack("NETStandard.Library.Ref", "2.1.0", "netstandard2.1");

        var gap = BcCompiler.DiagnoseDotNetRefPacks(RuntimeDir(), null);

        Assert.NotNull(gap);
        Assert.True(gap!.AliasesCannotBind);
        Assert.Contains("Microsoft.NETCore.App.Ref for .NET 8 is not installed", gap.Warning);
    }

    /// <summary>
    /// The issue's second question: only another major's pack is installed. The enumeration still
    /// falls back to it (behaviour unchanged), the warning says so — but this is NOT the AL0185
    /// shape, so a drop is not attributed to it.
    /// </summary>
    [Fact]
    public void OnlyAnotherMajorsPack_WarnsAboutTheFallback_ButDoesNotClaimAliasesFailToBind()
    {
        var core10 = AddPack("Microsoft.NETCore.App.Ref", "10.0.11", "net10.0");
        AddPack("NETStandard.Library.Ref", "2.1.0", "netstandard2.1");

        var gap = BcCompiler.DiagnoseDotNetRefPacks(RuntimeDir(), null);

        Assert.NotNull(gap);
        Assert.False(gap!.AliasesCannotBind);
        Assert.Contains("no .NET 8 version", gap.Warning);
        Assert.Contains("10.0.11", gap.Warning);
        Assert.Contains(core10, BcCompiler.EnumerateDotNetRefAssemblyDirsUnder(RuntimeDir(), null));
        Assert.Null(BcCompiler.AttributeToMissingDotNetRefPack(
            new[] { "error AL0185: DotNet 'XmlNode' is missing" }, gap));
    }

    [Fact]
    public void ARunningMajorPackBesideAnotherMajor_IsNotAGap()
    {
        AddPack("Microsoft.NETCore.App.Ref", "8.0.30", "net8.0");
        AddPack("Microsoft.NETCore.App.Ref", "10.0.11", "net10.0");
        AddPack("NETStandard.Library.Ref", "2.1.0", "netstandard2.1");

        Assert.Null(BcCompiler.DiagnoseDotNetRefPacks(RuntimeDir(), null));
    }

    [Fact]
    public void AnUnlocatableRoot_IsAGapNotASilentPass()
    {
        // No /shared/ segment in the runtime dir and no DOTNET_ROOT: nothing can be read.
        var gap = BcCompiler.DiagnoseDotNetRefPacks(Path.Combine(_root, "elsewhere", "8.0.30"), null);

        Assert.NotNull(gap);
        Assert.True(gap!.AliasesCannotBind);
        Assert.Contains("Could not locate the dotnet root", gap.Warning);
    }

    [Fact]
    public void DotnetRootEnvironmentVariable_IsUsedWhenTheRuntimePathHasNoSharedSegment()
    {
        AddPack("Microsoft.NETCore.App.Ref", "8.0.30", "net8.0");
        AddPack("NETStandard.Library.Ref", "2.1.0", "netstandard2.1");

        Assert.Null(BcCompiler.DiagnoseDotNetRefPacks(Path.Combine(_root, "elsewhere", "8.0.30"), _root));
    }

    private const string Al0185DotNet =
        "SourceFile(/tmp/Microsoft_Tests-TestLibraries/Xml.Codeunit.al@5:9): error AL0185: DotNet 'XmlNode' is missing";

    [Fact]
    public void ADotNetDrop_IsAttributedToTheMissingPacks_WithTheRootAndTheFix()
    {
        var gap = BcCompiler.DiagnoseDotNetRefPacks(RuntimeDir(), null);

        var cause = BcCompiler.AttributeToMissingDotNetRefPack(new[] { Al0185DotNet }, gap);

        Assert.NotNull(cause);
        Assert.Contains("not the AL", cause);
        Assert.Contains(Path.Combine(_root, "packs"), cause);
        Assert.Contains("Install the .NET SDK", cause);
    }

    [Fact]
    public void AnAl0185ForAnObject_OrAnotherCode_IsNotAttributed()
    {
        var gap = BcCompiler.DiagnoseDotNetRefPacks(RuntimeDir(), null);

        Assert.Null(BcCompiler.AttributeToMissingDotNetRefPack(
            new[] { "error AL0185: Codeunit 'Some Dep' is missing" }, gap));
        Assert.Null(BcCompiler.AttributeToMissingDotNetRefPack(
            new[] { Al0185DotNet.Replace("AL0185", "AL0133") }, gap));
        Assert.Null(BcCompiler.AttributeToMissingDotNetRefPack(Array.Empty<string>(), gap));
    }

    [Fact]
    public void WithoutAGap_NothingIsAttributed()
    {
        Assert.Null(BcCompiler.AttributeToMissingDotNetRefPack(new[] { Al0185DotNet }, null));
    }

    /// <summary>The attribution has to reach the message the dependency load prints.</summary>
    [Fact]
    public void TheDependencyDropMessage_CarriesTheCause_OnlyWhenThereIsAGap()
    {
        var gap = BcCompiler.DiagnoseDotNetRefPacks(RuntimeDir(), null);
        var objs = new[] { "Codeunit .\"Library - XML\"" };

        var with = DependencyLoader.BuildDependencyEmitExcludedDetail(objs, 202, new[] { Al0185DotNet }, gap);
        var without = DependencyLoader.BuildDependencyEmitExcludedDetail(objs, 202, new[] { Al0185DotNet }, null);

        Assert.Contains("1 of this dependency's 203 object(s)", with);   // the generic text survives
        Assert.Contains("Probable cause, not the AL", with);
        Assert.Contains(Path.Combine(_root, "packs"), with);
        Assert.DoesNotContain("Probable cause", without);
    }
}
