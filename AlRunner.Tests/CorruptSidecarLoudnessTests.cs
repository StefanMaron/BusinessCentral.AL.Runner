// CorruptSidecarLoudnessTests — #2750: a corrupt Tier-1 `.deps-bin` sidecar DLL must not be
// silent at DEFAULT verbosity.
//
// What was wrong
// --------------
// DependencyLoader.LoadOne probes for the Tier-1 sidecar with File.Exists, does
// Assembly.Load(bytes), catches any failure, wrote
//
//     [deps] tier-1 load failed for <Name>: <reason>
//
// to stderr, and fell through to Tier 2/3. That line never reached the terminal: Log's
// component-tag filter exempts `dep`, not `deps`, so `[deps] …` matched the generic tag
// pattern and was dropped unless --verbose. Measured on the
// tests/runner-extras/testpage-precompiled-dep-control fixture with a 5-byte bogus PE in
// its .deps-bin/: at default verbosity the whole run printed two startup lines, two
// "no loaded type Record65600 found" failures and the summary — no mention of the DLL it
// could not load. With --verbose the [deps] line was there all along.
//
// `dep` and `deps` are NOT the same category, so this is NOT a one-character fix to the
// exemption list -- see DepsIsADistinctCategoryTests below, which pins that decision:
//   [dep]  — DependencyResolver's per-dependency resolution results (which .app won a slot).
//            Already exempt, and documented in --help as the mechanical way to audit
//            resolution.
//   [deps] — DependencyLoader's tier-by-tier loading internals: source-cache HIT/WROTE,
//            compiled-on-the-fly timings, DLL-first codeunit counts, chunk counts, metadata
//            sidecar replay. High volume, and exactly what the filter exists to hide.
// Exempting `deps` wholesale would surface all of that on every run. So the fix is at the
// EMIT SITE: a sidecar that exists but cannot be loaded is a provisioning gap, not a
// diagnostic, and it is reported as one — loud now, and repeated in the run summary.
//
// This is the fifth instance of the same Log.cs exemption-list bug; the history of [bc],
// [expectations], [reexec], [dap] and [warn] is in Log.cs.
using AlRunner;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

[Collection(ConsoleFilterSerialCollection.Name)]
public sealed class CorruptSidecarLoudnessTests
{
    /// <summary>
    /// Push one line through the real Log filter and return what a user would actually see.
    /// </summary>
    private static string ThroughFilter(string line, bool verbose)
    {
        var savedOut = Console.Out;
        var savedErr = Console.Error;
        var savedVerbose = Log.Verbose;
        var sink = new StringWriter();
        try
        {
            Console.SetOut(sink);
            Console.SetError(sink);
            Log.Install();              // wraps the sink in the filtering writer
            Log.Verbose = verbose;
            Console.Error.WriteLine(line);
            return sink.ToString();
        }
        finally
        {
            Log.Verbose = savedVerbose;
            Console.SetOut(savedOut);
            Console.SetError(savedErr);
        }
    }

    private const string Publisher = "AL Runner Fixtures";
    private const string AppName = "TPCD Precompiled Control Dep";
    private const string AppVersion = "1.0.0.0";
    private const string SidecarPath =
        "/repo/tests/runner-extras/testpage-precompiled-dep-control/.deps-bin/"
        + "AL_Runner_Fixtures_TPCD_Precompiled_Control_Dep_1.0.0.0.dll";
    private const string Reason = "Bad IL format.";

    private static string Message() =>
        ProvisioningCheck.BuildPrecompiledSidecarLoadFailedMessage(
            Publisher, AppName, AppVersion, SidecarPath, Reason);

    [Fact]
    public void CorruptSidecarMessage_SurvivesTheFilterAtDefaultVerbosity()
    {
        // The whole defect: this is the ONE line that explains the run, and the user has to
        // see it without being told to re-run with --verbose. Note the verbosity assertion --
        // #2750 is specifically about DEFAULT verbosity, not about the line existing at all.
        var seen = ThroughFilter(Message(), verbose: false);

        Assert.Contains(SidecarPath, seen);
        Assert.Contains(AppName, seen);
        Assert.Contains(Reason, seen);
    }

    [Fact]
    public void CorruptSidecarMessage_NamesTheFileTheAppAndTheReason()
    {
        // Loud is not enough: "a dependency failed to load" sends the reader hunting. The
        // message has to identify WHICH file on disk, which app it was serving, and why the
        // load failed, or the fix is still a search.
        var message = Message();

        Assert.Contains(SidecarPath, message);
        Assert.Contains(Publisher, message);
        Assert.Contains(AppName, message);
        Assert.Contains(AppVersion, message);
        Assert.Contains(Reason, message);
        // And it must say what the consequence is, since the run continues on a lower tier
        // and the eventual failure looks unrelated ("no loaded type RecordNNNNN found").
        Assert.Contains(".deps-bin", message);
    }

    [Fact]
    public void TheOldDepsTaggedMessage_WasSuppressedAtDefaultVerbosity()
    {
        // REGRESSION PIN, and the reason the emit site had to change rather than the filter.
        // This is the literal string the pre-fix code wrote. It is invisible by default --
        // so anyone reintroducing that shape reintroduces the bug, and this test says so.
        var old = $"[deps] tier-1 load failed for {AppName}: {Reason}";

        Assert.DoesNotContain(old, ThroughFilter(old, verbose: false));
        Assert.Contains(old, ThroughFilter(old, verbose: true));
    }

}

/// <summary>
/// The summary half of #2750, split into its own class: ProvisionGapLog is process-global
/// state shared with ProvisionGapLogTests, so this belongs in THAT serial collection rather
/// than the console-filter one. Being loud at the point of discovery is only half the fix —
/// on a long run the discovery line scrolls thousands of lines above the summary the caller
/// actually reads (#2587).
/// </summary>
[Collection(RecordPatchesSerialCollection.Name)]
public sealed class CorruptSidecarGapSummaryTests
{
    [Fact]
    public void CorruptSidecarGap_IsRecordedForTheRunSummary()
    {
        var savedErr = Console.Error;
        try
        {
            Console.SetError(TextWriter.Null);
            ProvisionGapLog.Reset();
            ProvisionGapLog.Report(
                ProvisioningCheck.BuildPrecompiledSidecarLoadFailedMessage(
                    "AL Runner Fixtures", "TPCD Precompiled Control Dep", "1.0.0.0",
                    "/repo/.deps-bin/AL_Runner_Fixtures_TPCD_1.0.0.0.dll", "Bad IL format."));

            var collected = Assert.Single(ProvisionGapLog.Collected);
            Assert.Contains("AL_Runner_Fixtures_TPCD_1.0.0.0.dll", collected);
            Assert.Contains("Bad IL format.", collected);
        }
        finally
        {
            ProvisionGapLog.Reset();
            Console.SetError(savedErr);
        }
    }
}

/// <summary>
/// #2750: pins that `dep` and `deps` are two DIFFERENT categories, so adding `deps` to Log's
/// exemption list is the WRONG fix — it would surface DependencyLoader's whole tier-by-tier
/// commentary on every default-verbosity run. These are the real message shapes from
/// DependencyLoader.cs.
/// </summary>
[Collection(ConsoleFilterSerialCollection.Name)]
public sealed class DepsIsADistinctCategoryTests
{
    private static string ThroughFilter(string line, bool verbose)
    {
        var savedOut = Console.Out;
        var savedErr = Console.Error;
        var savedVerbose = Log.Verbose;
        var sink = new StringWriter();
        try
        {
            Console.SetOut(sink);
            Console.SetError(sink);
            Log.Install();
            Log.Verbose = verbose;
            Console.Error.WriteLine(line);
            return sink.ToString();
        }
        finally
        {
            Log.Verbose = savedVerbose;
            Console.SetOut(savedOut);
            Console.SetError(savedErr);
        }
    }

    [Theory]
    [InlineData("[deps] source-cache HIT: Sidecar Dep v1.0.0.0 key=0123456789ab (4096 bytes, 0 report-metadata entries, 0 enum-registry entries)")]
    [InlineData("[deps] source-cache WROTE: Sidecar Dep v1.0.0.0 key=0123456789ab (4096 bytes, 0 report-metadata entries, 0 enum-registry entries)")]
    [InlineData("[deps] compiled-on-the-fly: Sidecar Dep v1.0.0.0 (212ms).")]
    [InlineData("[deps] tier-2 R2R: Base Application loaded 5 DLL chunk(s)")]
    [InlineData("[deps] DLL-first: Microsoft_Library Assert v28.1 — 1 codeunit(s)")]
    public void ChattyLoaderDiagnostics_StaySuppressedByDefault(string line)
    {
        // If these ever start showing at default verbosity, someone "fixed" #2750 by
        // exempting `deps`, and every run now carries the loader's internal commentary.
        Assert.DoesNotContain(line, ThroughFilter(line, verbose: false));
        Assert.Contains(line, ThroughFilter(line, verbose: true));
    }

    [Fact]
    public void DepResolutionLines_StayVisibleByDefault()
    {
        // The other half of the distinction: `[dep]` is DependencyResolver's resolution
        // RESULT and is exempt. Pinned here so a fix for the above cannot quietly remove it.
        const string line = "[dep] note: symbols-only package won over a code-bearing one";

        Assert.Contains(line, ThroughFilter(line, verbose: false));
    }
}
