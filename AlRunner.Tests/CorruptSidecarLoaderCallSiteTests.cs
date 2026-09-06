// CorruptSidecarLoaderCallSiteTests — #2750, the half that binds the fix to the EMIT SITE.
//
// CorruptSidecarLoudnessTests (sibling file) proves things about the MESSAGE: that the
// builder names the file, the app and the reason, and that its `[provision-gap]` tag
// survives Log's default-verbosity filter. Every one of those tests constructs its subject
// by hand, so all of them stay green if someone puts
//
//     Console.Error.WriteLine($"[deps] tier-1 load failed for {m.Name}: {ex.Message}");
//
// back at the catch in DependencyLoader.LoadOne — which is the entire defect. Two different
// claims: "the message is loud" and "the loader emits that message". This file is the
// second one, and it gets it by actually RUNNING the loader over a corrupt sidecar rather
// than by asserting anything about the source text.
//
// Both catches are covered, because #2750's fix touched both and they are siblings:
//   Tier 1 — the `.deps-bin/<Publisher>_<Name>_<Version>.dll` precompiled sidecar. It was
//            preferred over every lower tier and then failed to load.
//   Tier 2 — an R2R `publishedartifacts/*.dll` chunk. Worse in one respect: the loop
//            continues, so `primary` can still come back non-null from another chunk and
//            the caller sees a successful load of an app missing part of itself.
//
// No BC artifacts, no subprocess: the Tier-1 catch runs before anything touches the
// compiler, and a dependency whose every tier fails ends in LoadOne's symbol-only branch,
// which returns cleanly. So this executes the real call site in milliseconds.
//
// There is no claim about Business Central anywhere in this file — it is entirely about
// where the runner routes one of its own diagnostics — so nothing here belongs upstream in
// the al-language corpus.
using System;
using System.IO;
using System.IO.Compression;
using AlRunner;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// In <see cref="RecordPatchesSerialCollection"/> because <see cref="ProvisionGapLog"/> is
/// process-global state shared with ProvisionGapLogTests / ProvisionGapSummaryTests: a
/// concurrent Reset between this class's Report and its read would empty the list under it.
/// Deliberately does NOT swap Console.Out/Console.Error — see the note in
/// ConsoleFilterSerialCollection.cs on why a class in this collection must not.
/// </summary>
[Collection(RecordPatchesSerialCollection.Name)]
public sealed class CorruptSidecarLoaderCallSiteTests : IDisposable
{
    private const string Publisher = "CorruptSidecarFixture";
    private const string AppName = "TierDep";
    private static readonly Version AppVersion = new(1, 0, 0, 0);

    // Enough of a DOS header to look like it wants to be a PE, and far too short to be one.
    // The same 5-byte shape the #2750 investigation dropped into the real
    // tests/runner-extras/testpage-precompiled-dep-control/.deps-bin/ fixture.
    private static readonly byte[] BogusPeBytes = { 0x4D, 0x5A, 0x90, 0x00, 0x03 };

    private readonly string _root =
        TestScratch.Dir("al-runner-corrupt-sidecar-callsite");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static AppManifest Manifest() =>
        new(Publisher, AppName, AppVersion, Guid.NewGuid(), Array.Empty<DependencyRef>());

    /// <summary>
    /// A syntactically valid, EMPTY package. Carries no `publishedartifacts/*.dll` and no
    /// `src/*.al`, so once the corrupt sidecar has failed, Tier 2 and Tier 3 both decline
    /// and LoadOne returns through its symbol-only branch instead of throwing. That keeps
    /// the assertions below about the catch we care about and nothing else.
    /// </summary>
    private string WriteEmptyApp(string fileName)
    {
        Directory.CreateDirectory(_root);
        var appPath = Path.Combine(_root, fileName);
        using (ZipFile.Open(appPath, ZipArchiveMode.Create)) { }
        return appPath;
    }

    /// <summary>
    /// Assert that <paramref name="gap"/> is verbatim what
    /// <see cref="ProvisioningCheck.BuildPrecompiledSidecarLoadFailedMessage"/> produces for
    /// these inputs, with only the reason line left open (the exact
    /// BadImageFormatException text is runtime- and culture-dependent).
    ///
    /// Comparing against the BUILDER rather than against a literal is what ties this file to
    /// CorruptSidecarLoudnessTests: that class proves this exact text survives the default
    /// filter, so together the two say "the loader emits a message the user actually sees".
    /// A literal here would let the two drift apart silently.
    /// </summary>
    private static void AssertIsTheSidecarGapMessage(string gap, string expectedPath)
    {
        const string sentinel = "<<reason>>";
        var expected = ProvisioningCheck.BuildPrecompiledSidecarLoadFailedMessage(
            Publisher, AppName, AppVersion.ToString(), expectedPath, sentinel)
            .Split(Environment.NewLine);
        var actual = gap.Split(Environment.NewLine);

        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            if (expected[i].Contains(sentinel, StringComparison.Ordinal))
            {
                // The reason line: same prefix, and a NON-EMPTY reason. "Reason: " with
                // nothing after it would satisfy a prefix check and tell the reader nothing.
                var prefix = expected[i][..expected[i].IndexOf(sentinel, StringComparison.Ordinal)];
                Assert.StartsWith(prefix, actual[i], StringComparison.Ordinal);
                Assert.NotEqual(prefix, actual[i]);
            }
            else
            {
                Assert.Equal(expected[i], actual[i]);
            }
        }

        // The pre-fix shape, spelled out so a reviewer can see what this test forbids.
        Assert.DoesNotContain("[deps]", gap, StringComparison.Ordinal);
        Assert.StartsWith("[provision-gap] ", gap, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadingADependencyWithACorruptTier1Sidecar_ReportsAProvisioningGap()
    {
        var appPath = WriteEmptyApp($"{Publisher}_{AppName}_{AppVersion}.app");
        var depsBin = Path.Combine(_root, ".deps-bin");
        Directory.CreateDirectory(depsBin);
        // The name DependencyLoader.FindPrecompiledSidecar probes for. Publisher and name are
        // chosen free of characters SanitizeFileName rewrites, so this is the literal probe.
        var sidecar = Path.Combine(depsBin, $"{Publisher}_{AppName}_{AppVersion}.dll");
        File.WriteAllBytes(sidecar, BogusPeBytes);

        ProvisionGapLog.Reset();
        var loaded = new DependencyLoader(null!, null!)
            .LoadAll(new[] { (Manifest(), appPath) }, _root);

        // Nothing served the dependency: the sidecar was preferred over every lower tier and
        // did not load. This is the state the pre-fix code reached in complete silence.
        Assert.Empty(loaded);

        var gap = Assert.Single(ProvisionGapLog.Collected);
        AssertIsTheSidecarGapMessage(gap, sidecar);
    }

    [Fact]
    public void LoadingADependencyWithACorruptR2RChunk_ReportsAProvisioningGap()
    {
        Directory.CreateDirectory(_root);
        var appPath = Path.Combine(_root, $"{Publisher}_{AppName}_R2R_{AppVersion}.app");
        using (var zip = ZipFile.Open(appPath, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("publishedartifacts/chunk000.dll");
            using var s = entry.Open();
            s.Write(BogusPeBytes, 0, BogusPeBytes.Length);
        }

        ProvisionGapLog.Reset();
        var loaded = new DependencyLoader(null!, null!)
            .LoadAll(new[] { (Manifest(), appPath) }, _root);

        // The Tier-2 loop swallows a bad chunk and carries on, so `primary` — and therefore
        // the whole app — can come back looking fine. Here there is only one chunk, so the
        // observable end state is the same "nothing loaded" as Tier 1.
        Assert.Empty(loaded);

        var gap = Assert.Single(ProvisionGapLog.Collected);

        // The Tier-2 catch must report the extracted CHUNK, not the .app: that is the file
        // whose bytes did not load, and the only one an operator can inspect. The chunk is
        // published into the r2r-chunks cache under a positional name (000.dll), so asserting
        // the .app path here would pass against a message naming the wrong file.
        var found = ExtractFoundPath(gap);
        Assert.NotEqual(appPath, found);
        Assert.EndsWith(".dll", found, StringComparison.Ordinal);
        Assert.Contains("r2r-chunks", found, StringComparison.Ordinal);
        Assert.True(File.Exists(found), $"the reported chunk must exist on disk: {found}");
        AssertIsTheSidecarGapMessage(gap, found);

        // The chunk cache is content-addressed under the shared cache root, so clean up the
        // one entry this test minted rather than leaving it behind on every run.
        try { Directory.Delete(Path.GetDirectoryName(found)!, recursive: true); } catch { }
    }

    /// <summary>The `  Found:  &lt;path&gt;` line's payload.</summary>
    private static string ExtractFoundPath(string gap)
    {
        const string marker = "  Found:  ";
        var line = gap.Split(Environment.NewLine)[1];
        Assert.StartsWith(marker, line, StringComparison.Ordinal);
        return line[marker.Length..];
    }

    /// <summary>
    /// The control, and the reason the two tests above mean anything. A dependency with NO
    /// sidecar and no loadable tier at all is the ordinary symbol-only case — healthy, and
    /// emphatically not a provisioning gap. Without this, moving
    /// <c>ProvisionGapLog.Report</c> out of the catch and calling it unconditionally on
    /// every Tier-1 probe would satisfy both tests above.
    /// </summary>
    [Fact]
    public void LoadingASymbolOnlyDependencyWithNoSidecar_ReportsNoGap()
    {
        var appPath = WriteEmptyApp($"{Publisher}_{AppName}_SymbolOnly_{AppVersion}.app");
        // No .deps-bin at all, so FindPrecompiledSidecar returns null and the catch is
        // never entered.
        Assert.False(Directory.Exists(Path.Combine(_root, ".deps-bin")));

        ProvisionGapLog.Reset();
        var loaded = new DependencyLoader(null!, null!)
            .LoadAll(new[] { (Manifest(), appPath) }, _root);

        Assert.Empty(loaded);
        Assert.Empty(ProvisionGapLog.Collected);
    }
}
