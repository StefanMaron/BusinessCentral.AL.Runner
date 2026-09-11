// ArtifactDirStateTests — #3878. A partially-provisioned artifact directory used to be
// indistinguishable from a complete one until a consumer failed deep inside an assembly
// load, so the failure looked like a code fault.
//
// The three shapes these pin are the ones measured on the reporting box, not invented:
//
//   27.5.46862.48827   82 DLLs, Ncl.dll PRESENT, closure sentinel absent   -> Partial
//   28.0.46665.54452    0 DLLs, Ncl.dll absent, holds only platform-apps/  -> Partial
//   28.4.53241.54407  501 DLLs, everything present                         -> Complete
//   (a version never provisioned at all)                                   -> Absent
//
// The two broken shapes are NOT the same failure, which is the whole point of the
// classification: the 82-DLL directory passes an "is this a service-tier directory"
// check (it carries Ncl.dll) and fails later, deeper and less legibly; the empty one is
// correctly rejected by such a check. A guard built only for the first does not fire on
// the second.

using Xunit;
using AlRunner.Infrastructure;

namespace AlRunner.Tests;

public sealed class ArtifactDirStateTests : IDisposable
{
    private readonly string _root;

    public ArtifactDirStateTests()
    {
        _root = TestScratch.Dir("al-runner-artifact-state");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string VersionDir(string version)
    {
        var d = Path.Combine(_root, version);
        Directory.CreateDirectory(d);
        return d;
    }

    private static void Touch(string dir, string name)
        => File.WriteAllText(Path.Combine(dir, name), "x");

    /// <summary>The complete engine closure: the five core engine DLLs + the closure sentinel.</summary>
    private static void WriteCompleteClosure(string dir)
    {
        foreach (var f in new[]
        {
            "Microsoft.Dynamics.Nav.Ncl.dll",
            "Microsoft.Dynamics.Nav.Types.dll",
            "Microsoft.Dynamics.Nav.Common.dll",
            "Microsoft.Dynamics.Nav.Language.dll",
            "Microsoft.Dynamics.Nav.CodeAnalysis.dll",
            "Microsoft.Identity.ServiceEssentials.Core.dll",
        }) Touch(dir, f);
    }

    // ── The four states, each asserted on the shape that produces it ──────────

    [Fact]
    public void Classify_CompleteClosure_IsComplete()
    {
        var dir = VersionDir("28.4.53241.54407");
        WriteCompleteClosure(dir);

        var state = ArtifactDirState.Classify(dir);

        Assert.Equal(ArtifactDirStatus.Complete, state.Status);
        Assert.True(state.IsUsable);
        Assert.Empty(state.MissingFiles);
    }

    /// <summary>
    /// The issue's subject: Ncl.dll present, so every "is this a service-tier directory"
    /// check passes, but the closure is short. Must be Partial — NOT Absent, and NOT
    /// Complete.
    /// </summary>
    [Fact]
    public void Classify_NclPresentButClosureShort_IsPartial_NotAbsent()
    {
        var dir = VersionDir("27.5.46862.48827");
        WriteCompleteClosure(dir);
        File.Delete(Path.Combine(dir, "Microsoft.Identity.ServiceEssentials.Core.dll"));

        var state = ArtifactDirState.Classify(dir);

        Assert.Equal(ArtifactDirStatus.Partial, state.Status);
        Assert.False(state.IsUsable);
        // The directory LOOKS like a service tier — that is exactly why it is dangerous.
        Assert.True(state.HasEngineEntrypoint);
        Assert.Contains("Microsoft.Identity.ServiceEssentials.Core.dll", state.MissingFiles);
        // ...and it is not confused with the empty shape below.
        Assert.Single(state.MissingFiles);
    }

    /// <summary>
    /// The second broken shape the coordinator measured and the issue body does not name:
    /// zero DLLs, no Ncl.dll, only a platform-apps/ subdirectory. It must classify as
    /// Partial too — the directory exists and something put it there — and must be
    /// distinguishable from the 82-DLL shape by HasEngineEntrypoint.
    /// </summary>
    [Fact]
    public void Classify_EmptyButForPlatformApps_IsPartial_AndHasNoEngineEntrypoint()
    {
        var dir = VersionDir("28.0.46665.54452");
        Directory.CreateDirectory(Path.Combine(dir, "platform-apps"));

        var state = ArtifactDirState.Classify(dir);

        Assert.Equal(ArtifactDirStatus.Partial, state.Status);
        Assert.False(state.IsUsable);
        Assert.False(state.HasEngineEntrypoint);
        Assert.Contains("Microsoft.Dynamics.Nav.Ncl.dll", state.MissingFiles);
        // All six, not one: this is a categorically worse shape than the 82-DLL one.
        Assert.Equal(6, state.MissingFiles.Count);
    }

    /// <summary>
    /// The constraint from guards-need-a-third-state.md: "a genuinely absent thing must
    /// stay a legitimate state". A version that was never provisioned is Absent, and
    /// Absent is NOT Partial — the remedy differs (provision it, vs. repair/re-fetch a
    /// directory something already wrote to).
    /// </summary>
    [Fact]
    public void Classify_NeverProvisioned_IsAbsent_NotPartial()
    {
        var dir = Path.Combine(_root, "28.9.99999.99999");

        var state = ArtifactDirState.Classify(dir);

        Assert.Equal(ArtifactDirStatus.Absent, state.Status);
        Assert.False(state.IsUsable);
        Assert.NotEqual(ArtifactDirStatus.Partial, state.Status);
    }

    /// <summary>
    /// The distinction the existing ProvisioningCheck.Check cannot draw: a directory that
    /// does not exist and one that exists but is empty both produce "all six missing".
    /// Same missing-file list, different status, different remedy.
    /// </summary>
    [Fact]
    public void Classify_AbsentAndEmpty_ShareAMissingListButNotAStatus()
    {
        var absent = Path.Combine(_root, "28.9.99999.99999");
        var empty = VersionDir("28.0.46665.54452");

        var a = ArtifactDirState.Classify(absent);
        var e = ArtifactDirState.Classify(empty);

        Assert.Equal(a.MissingFiles.Count, e.MissingFiles.Count);
        Assert.NotEqual(a.Status, e.Status);
    }

    // ── The third state: could not measure ───────────────────────────────────

    /// <summary>
    /// guards-need-a-third-state.md, point 5: "prove the third state fires". A path that
    /// exists but is not a directory cannot be measured — it is neither a legitimately
    /// absent version nor a partial one — so it must report Unreadable rather than
    /// resolving toward either Complete (a false green) or Absent (which would send the
    /// reader to `provision`, a remedy that cannot fix a file sitting where a directory
    /// belongs).
    /// </summary>
    [Fact]
    public void Classify_PathIsAFileNotADirectory_IsUnreadable()
    {
        var path = Path.Combine(_root, "28.4.53241.54407");
        File.WriteAllText(path, "not a directory");

        var state = ArtifactDirState.Classify(path);

        Assert.Equal(ArtifactDirStatus.Unreadable, state.Status);
        Assert.False(state.IsUsable);
        Assert.NotEqual(ArtifactDirStatus.Absent, state.Status);
        Assert.NotEqual(ArtifactDirStatus.Complete, state.Status);
    }

    [Fact]
    public void Classify_UnreadableNamesWhatCouldNotBeEstablished()
    {
        var path = Path.Combine(_root, "28.4.53241.54407");
        File.WriteAllText(path, "not a directory");

        var state = ArtifactDirState.Classify(path);

        // The message must send the reader to the right remedy, which for this shape is
        // "something is in the way", never "download it again".
        Assert.Contains("not a directory", state.Explain(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(path, state.Explain());
    }

    // ── The message each broken shape produces ───────────────────────────────

    [Fact]
    public void Explain_Partial_NamesTheDirectoryAndTheMissingFile()
    {
        var dir = VersionDir("27.5.46862.48827");
        WriteCompleteClosure(dir);
        File.Delete(Path.Combine(dir, "Microsoft.Identity.ServiceEssentials.Core.dll"));

        var text = ArtifactDirState.Classify(dir).Explain();

        // Names the directory, so the reader knows WHICH one is broken — the thing three
        // separate diagnoses had to re-derive.
        Assert.Contains(dir, text);
        Assert.Contains("Microsoft.Identity.ServiceEssentials.Core.dll", text);
        Assert.Contains("partially provisioned", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Explain_Absent_DoesNotClaimTheDirectoryIsBroken()
    {
        var dir = Path.Combine(_root, "28.9.99999.99999");

        var text = ArtifactDirState.Classify(dir).Explain();

        Assert.Contains(dir, text);
        // An absent version is a legitimate state; calling it "partially provisioned"
        // would send the reader looking for corruption that is not there.
        Assert.DoesNotContain("partially provisioned", text, StringComparison.OrdinalIgnoreCase);
    }

    // ── Scanning a whole artifacts root ──────────────────────────────────────

    /// <summary>
    /// What a cycle-start probe needs: every version directory under the root, classified,
    /// with the broken ones nameable without opening an assembly. Reproduces the box's
    /// measured shape — two broken among healthy ones.
    /// </summary>
    [Fact]
    public void ScanRoot_NamesOnlyTheBrokenDirectories()
    {
        WriteCompleteClosure(VersionDir("28.4.53241.54407"));
        WriteCompleteClosure(VersionDir("27.5.46862.53931"));

        var short82 = VersionDir("27.5.46862.48827");
        WriteCompleteClosure(short82);
        File.Delete(Path.Combine(short82, "Microsoft.Identity.ServiceEssentials.Core.dll"));

        Directory.CreateDirectory(Path.Combine(VersionDir("28.0.46665.54452"), "platform-apps"));

        var results = ArtifactDirState.ScanRoot(_root);

        Assert.Equal(4, results.Count);
        var broken = results.Where(r => !r.IsUsable).Select(r => Path.GetFileName(r.Directory)).OrderBy(x => x).ToList();
        Assert.Equal(new[] { "27.5.46862.48827", "28.0.46665.54452" }, broken);

        var healthy = results.Where(r => r.IsUsable).Select(r => Path.GetFileName(r.Directory)).OrderBy(x => x).ToList();
        Assert.Equal(new[] { "27.5.46862.53931", "28.4.53241.54407" }, healthy);
    }

    /// <summary>
    /// The constraint again, at root level: an artifacts root that does not exist is a
    /// legitimate state (nothing provisioned yet) and yields no results rather than
    /// throwing. A fix that hard-errors here would swap a false green for a false red.
    /// </summary>
    [Fact]
    public void ScanRoot_MissingRoot_IsEmpty_NotAThrow()
    {
        var results = ArtifactDirState.ScanRoot(Path.Combine(_root, "no-such-root"));

        Assert.Empty(results);
    }

    /// <summary>
    /// Non-version-named entries under the root (the test-data dir, a stray file) are not
    /// artifact directories and must not be reported as broken ones.
    /// </summary>
    [Fact]
    public void ScanRoot_IgnoresNonVersionNamedEntries()
    {
        WriteCompleteClosure(VersionDir("28.4.53241.54407"));
        Directory.CreateDirectory(Path.Combine(_root, "scratch"));
        File.WriteAllText(Path.Combine(_root, "README.txt"), "x");

        var results = ArtifactDirState.ScanRoot(_root);

        Assert.Single(results);
        Assert.Equal("28.4.53241.54407", Path.GetFileName(results[0].Directory));
    }

    // ── Against the real artifacts root, when one exists ─────────────────────

    /// <summary>
    /// #3878's actual subject: the directories on the developer's own disk. Skips on a
    /// machine with no artifacts root (CI legs provision into a different layout), so it
    /// is a local sharpener rather than a CI gate — but where a root DOES exist, every
    /// directory it classifies Complete must really hold the engine entrypoint, and every
    /// Partial one must name at least one missing file. A classifier that answered
    /// Complete for a directory without Ncl.dll would be exactly the false green this
    /// change exists to remove.
    /// </summary>
    [Fact]
    public void ScanRoot_OnTheRealArtifactsRoot_NeverReportsCompleteWithoutTheEngine()
    {
        var root = Environment.GetEnvironmentVariable(BcArtifacts.ArtifactsRootEnvVar);
        if (string.IsNullOrWhiteSpace(root))
            root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                BcArtifacts.ArtifactsRoot_Rel);
        if (!Directory.Exists(root)) return;   // nothing provisioned here; nothing to assert

        foreach (var r in ArtifactDirState.ScanRoot(root))
        {
            if (r.Status == ArtifactDirStatus.Complete)
            {
                Assert.True(r.HasEngineEntrypoint,
                    $"{r.Directory} classified Complete without Microsoft.Dynamics.Nav.Ncl.dll");
                Assert.Empty(r.MissingFiles);
            }
            else if (r.Status == ArtifactDirStatus.Partial)
            {
                Assert.NotEmpty(r.MissingFiles);
                Assert.Contains("partially provisioned", r.Explain(), StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>
    /// #2226's signature, found while answering "do two code paths write the same state
    /// with different invariants?" — and the real explanation of the second broken
    /// directory on the reporting box. `provision`'s platform-app sub-step resolved build
    /// 28.0.46665.54452 while its service-tier sub-step resolved 28.0.46665.54338, so one
    /// major.minor is split across two directories, each complete for its own half:
    ///
    ///   28.0.46665.54338   Ncl.dll present, platform-apps/ absent
    ///   28.0.46665.54452   Ncl.dll absent,  platform-apps/ holding all 6 apps (120 MB)
    ///
    /// Still Partial — it is not a usable service tier — but the message must not imply
    /// corruption, because nothing here is corrupt and the payload is genuinely usable.
    /// This is also why the directory survives AutoProvision's RemoveIfEmpty cleanup:
    /// that only deletes a directory holding NOTHING, and this one holds platform-apps/.
    /// </summary>
    [Fact]
    public void Classify_PayloadOnlyDir_IsPartial_ButNotDescribedAsCorrupt()
    {
        var dir = VersionDir("28.0.46665.54452");
        var payload = Path.Combine(dir, "platform-apps");
        Directory.CreateDirectory(payload);
        File.WriteAllText(Path.Combine(payload, "Microsoft_Base Application_28.0.46665.54452.app"), "x");

        var state = ArtifactDirState.Classify(dir);

        Assert.Equal(ArtifactDirStatus.Partial, state.Status);
        Assert.False(state.HasEngineEntrypoint);
        Assert.True(state.HasSiblingProvisionPayload);

        var text = state.Explain();
        Assert.Contains("#2226", text);
        Assert.Contains("payload here is usable", text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The negative half: a directory with neither engine DLLs nor a payload is the plain
    /// empty-leftover shape and must NOT claim the #2226 split, which would be a diagnosis
    /// nothing measured.
    /// </summary>
    [Fact]
    public void Classify_TrulyEmptyDir_DoesNotClaimTheSplitBuildCause()
    {
        var dir = VersionDir("28.0.46665.54452");

        var state = ArtifactDirState.Classify(dir);

        Assert.Equal(ArtifactDirStatus.Partial, state.Status);
        Assert.False(state.HasSiblingProvisionPayload);
        Assert.DoesNotContain("#2226", state.Explain());
    }
}
