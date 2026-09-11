// EngineClosureSharingTests — issue #3893.
//
// #3889 built ArtifactDirState, a four-way classification of a BC artifact directory over the
// engine closure. It shipped with NO non-test consumer: three entry points still opened a
// broken directory and failed deep, and tools/preflight.py re-implemented the closure list in
// Python rather than calling the C# definition.
//
// The hard part was never the predicate — it was reach. ProvisioningCheck is 1,997 lines whose
// closure pulls AppLoader, SafeDirectoryScan and BcArtifacts, and tools/metadata-ground-truth
// is deliberately outside AlRunner.slnx (its own header: a reference "would put a compile of a
// Microsoft app on every dotnet build"). So the tool could not ask the question and opened the
// directory regardless. EngineClosure.cs exists to be linkable: System.IO and nothing else.
//
// These tests pin the two properties that keep it that way — the tool still links it, and the
// runner still routes through it rather than keeping a second copy of the list.

using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class EngineClosureSharingTests : IDisposable
{
    private readonly string _dir;

    public EngineClosureSharingTests()
    {
        _dir = TestScratch.Dir("al-runner-engine-closure");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static string RepoRoot()
    {
        var d = AppContext.BaseDirectory;
        while (d != null && !File.Exists(Path.Combine(d, "AlRunner.slnx")))
            d = Path.GetDirectoryName(d);
        Assert.NotNull(d);
        return d!;
    }

    private void WriteCompleteClosure()
    {
        foreach (var f in EngineClosure.CoreEngineDlls)
            File.WriteAllText(Path.Combine(_dir, f), "x");
        File.WriteAllText(Path.Combine(_dir, EngineClosure.ClosureSentinel), "x");
    }

    // ── the #3878 shape, which is the reason a DLL count is not the predicate ────────────

    /// <summary>
    /// 27.5.46862.48827's exact shape: it carries Ncl.dll (and 81 other DLLs) and is short
    /// exactly the closure sentinel. Every "is this a service-tier directory" check — the
    /// glob the provision entry guard used, the Ncl.dll probe a consumer would write — passes
    /// on it, which is why the failure used to move to an assembly load instead of the
    /// surface.
    /// </summary>
    [Fact]
    public void CarriesNclButNotTheSentinel_IsIncomplete_AndNamesTheSentinel()
    {
        foreach (var f in EngineClosure.CoreEngineDlls)
            File.WriteAllText(Path.Combine(_dir, f), "x");

        var missing = EngineClosure.MissingFiles(_dir);

        Assert.False(EngineClosure.IsComplete(_dir));
        Assert.Equal(new[] { EngineClosure.ClosureSentinel }, missing);
        // The checks that pass on this directory — the defect, asserted directly.
        Assert.True(File.Exists(Path.Combine(_dir, "Microsoft.Dynamics.Nav.Ncl.dll")));
        Assert.True(Directory.EnumerateFiles(_dir, "*.dll").Any());
    }

    [Fact]
    public void CompleteClosure_IsComplete_AndNothingIsMissing()
    {
        WriteCompleteClosure();

        Assert.True(EngineClosure.IsComplete(_dir));
        Assert.Empty(EngineClosure.MissingFiles(_dir));
    }

    /// <summary>
    /// 28.0.46665.54452's shape — zero engine DLLs, a platform-apps payload only (#2226's
    /// split build). All six required files are named, not just the first.
    /// </summary>
    [Fact]
    public void EmptyDirectory_ReportsEverySixRequiredFile()
    {
        var missing = EngineClosure.MissingFiles(_dir);

        Assert.Equal(EngineClosure.CoreEngineDlls.Length + 1, missing.Count);
        Assert.Contains(EngineClosure.ClosureSentinel, missing);
        foreach (var f in EngineClosure.CoreEngineDlls) Assert.Contains(f, missing);
    }

    [Fact]
    public void MissingDirectory_ReportsEverySixRequiredFile_RatherThanThrowing()
    {
        var gone = Path.Combine(_dir, "nope");

        var missing = EngineClosure.MissingFiles(gone);

        Assert.Equal(EngineClosure.CoreEngineDlls.Length + 1, missing.Count);
        Assert.False(EngineClosure.IsComplete(gone));
    }

    // ── the reach properties #3893 is actually about ─────────────────────────────────────

    /// <summary>
    /// The gap #3893 reports, as a test: tools/metadata-ground-truth must be able to ask the
    /// question. It cannot take a ProjectReference on AlRunner (its own csproj header says
    /// why), so it links this one file. Deleting the link restores the fail-deep-inside-
    /// Assembly.Load behaviour with nothing failing, which is exactly how #3889 shipped with
    /// no consumer at all.
    /// </summary>
    [Fact]
    public void MetadataGroundTruth_LinksEngineClosure_AndTakesNoProjectReferenceOnAlRunner()
    {
        var csproj = Path.Combine(RepoRoot(), "tools", "metadata-ground-truth",
            "MetadataGroundTruth.csproj");
        Assert.True(File.Exists(csproj), csproj);
        var text = File.ReadAllText(csproj);

        Assert.Contains("EngineClosure.cs", text);
        // The constraint that made a link necessary rather than a reference. Matched as the
        // XML ELEMENT, not the bare word — the csproj comment above the link explains why a
        // reference is wrong and therefore contains the word itself.
        Assert.DoesNotContain("<ProjectReference", text);
    }

    /// <summary>
    /// The tool's entry point must consult the closure before BcHost.Install opens anything.
    /// Ordering is the whole claim: a check after the install measures a process that has
    /// already failed.
    /// </summary>
    [Fact]
    public void MetadataGroundTruth_ChecksTheClosure_BeforeInstallingTheBcHost()
    {
        var program = Path.Combine(RepoRoot(), "tools", "metadata-ground-truth", "Program.cs");
        var text = File.ReadAllText(program);

        var check = text.IndexOf("EngineClosure.MissingFiles", StringComparison.Ordinal);
        var install = text.IndexOf("BcHost.Install", StringComparison.Ordinal);

        Assert.True(check >= 0, "tools/metadata-ground-truth must consult EngineClosure (#3893)");
        Assert.True(install >= 0);
        Assert.True(check < install,
            $"the closure check (index {check}) must precede BcHost.Install (index {install}) — "
            + "a check after the install measures a process that has already failed");
    }

    /// <summary>
    /// ProvisioningCheck.Check must route through EngineClosure rather than keep a second
    /// copy of the list. Two copies is the state #3893 reports one level up (preflight.py's
    /// Python re-implementation): they agree until one is edited, and nothing says which.
    /// Asserted behaviourally — the two must agree on a directory short exactly one file.
    /// </summary>
    [Fact]
    public void ProvisioningCheckAndEngineClosure_AgreeOnTheSameDirectory()
    {
        foreach (var f in EngineClosure.CoreEngineDlls)
            File.WriteAllText(Path.Combine(_dir, f), "x");

        var report = ProvisioningCheck.Check("28.1.49838.53910", _dir);

        Assert.False(report.Ok);
        Assert.Equal(EngineClosure.MissingFiles(_dir), report.MissingFiles);

        WriteCompleteClosure();
        Assert.True(ProvisioningCheck.Check("28.1.49838.53910", _dir).Ok);
        Assert.True(EngineClosure.IsComplete(_dir));
    }

    /// <summary>
    /// ArtifactDirState — #3889's classifier, and the predicate the provision entry guard now
    /// uses — must agree with EngineClosure, since it delegates through ProvisioningCheck.
    /// </summary>
    [Fact]
    public void ArtifactDirState_AgreesWithEngineClosure_OnBothDirections()
    {
        Assert.False(ArtifactDirState.Classify(_dir).IsUsable);
        Assert.False(EngineClosure.IsComplete(_dir));

        WriteCompleteClosure();

        Assert.True(ArtifactDirState.Classify(_dir).IsUsable);
        Assert.True(EngineClosure.IsComplete(_dir));
    }
}
