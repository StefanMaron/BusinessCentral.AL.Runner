// DependencyEmitExclusionLoudnessTests — #2247: a dependency that emits only SOME of its
// objects must be loud, not loaded partially in silence.
//
// What was wrong
// --------------
// Program.cs's bundled-mode path has failed the run on emit-retry exclusion since #1991
// (EMIT-EXCLUDED), because losing an object silently shrinks what the run covers.
// DependencyLoader.LoadOne called the same BcCompiler.Emit and read only `.Sources`, so a
// dependency whose AL hit the same atomic-per-module emit crash was recovered partially,
// loaded, CACHED, and reported as a clean success.
//
// Measured on the fixture beside this file (AlRunner.Tests/Fixtures/DepEmitExclusion), BC
// 28.1.49838.54308, with the compiled-deps cache cleared first — the dependency .app ships two
// codeunits and one cannot bind:
//
//   before:  exit 0, "1P/0F/0E across 1 tests", and ZERO lines at default verbosity
//            mentioning the dropped object, EMIT-EXCLUDED, object 70861 or AL0185 (grep -c
//            over the whole run log: 0). The cached dependency DLL contained Codeunit70860
//            and not Codeunit70861, and the .object-metadata.json sidecar listed exactly one
//            object where the .app shipped two — the drop confirmed a second way, off disk,
//            rather than from the absence of a log line.
//   after:   exit 1, one [dep-load-fail] … EMIT-EXCLUDED line at DEFAULT verbosity naming the
//            dropped object, the 1-of-2 denominator, the consequence and the AL0185 that
//            caused it. No cache entry is written at all.
//
// The partial case is the whole point. A test asserting only that a dependency which fails
// ENTIRELY is loud would have passed before this fix: EMIT-ZERO already covered that, and it
// is the discontinuity — 10 of 10 lost is fatal, 9 of 10 lost is fine — that made the gap
// invisible.
//
// #3875 measured the same silent-partial shape one layer over, on the metadata path: Business
// Foundation produced 55 documents instead of 70, reported success, and printed zero AL0185
// lines at default verbosity. That path is fixed in the same PR
// (DependencyMetadataProducer.Ensure, METADATA-EMIT-EXCLUDED) and pinned below.
//
// See .claude/rules/guards-need-a-third-state.md: "some objects were excluded" had no verdict
// distinct from "everything emitted", so it was reported as the success state.

using AlRunner;
using Xunit;

namespace AlRunner.Tests;

public sealed class DependencyEmitExclusionMessageTests
{
    private static string Message(
        string[] excluded, int emitted, string[]? diagnostics = null)
        => DependencyLoader.BuildDependencyEmitExcludedDetail(
            excluded, emitted, diagnostics ?? Array.Empty<string>());

    /// <summary>
    /// The PARTIAL case, which is the one that hid. The message has to carry a denominator:
    /// "some objects were excluded" leaves the reader unable to tell a cosmetic drop from
    /// losing most of the app, and a count with no total is the same problem
    /// (#3875 measured 55 of 70 — the "of 70" is what made it a finding).
    /// </summary>
    [Fact]
    public void PartialExclusion_StatesWhatWasDroppedAndOutOfHowMany()
    {
        var msg = Message(new[] { "Codeunit \"DEX Dep Broken\"" }, emitted: 1);

        // The specific object, not just a count.
        Assert.Contains("DEX Dep Broken", msg, StringComparison.Ordinal);
        // The denominator: 1 dropped, 2 total, 1 survived.
        Assert.Contains("1 of this dependency's 2 object(s)", msg, StringComparison.Ordinal);
        Assert.Contains("only 1 of them", msg, StringComparison.Ordinal);
        // And the consequence, which is why a reader should care about a dependency at all.
        Assert.Contains("NavNCLMissingMethodException", msg, StringComparison.Ordinal);
    }

    /// <summary>
    /// The denominator has to track the real numbers rather than being a fixed string — a
    /// message that says "1 of 2" for every input is not reporting a measurement. Losing 15
    /// of 70 is #3875's shape, and it must render as exactly that.
    /// </summary>
    [Fact]
    public void TheDenominatorIsComputed_NotHardcoded()
    {
        var fifteen = Enumerable.Range(1, 15).Select(i => $"Table \"T{i}\"").ToArray();
        var msg = Message(fifteen, emitted: 55);

        Assert.Contains("15 of this dependency's 70 object(s)", msg, StringComparison.Ordinal);
        Assert.Contains("only 55 of them", msg, StringComparison.Ordinal);
        // Every dropped object is named, not just the first or a truncated sample: a reader
        // chasing one specific missing table must be able to find it here.
        Assert.Contains("Table \"T1\"", msg, StringComparison.Ordinal);
        Assert.Contains("Table \"T15\"", msg, StringComparison.Ordinal);
    }

    /// <summary>
    /// The AL diagnostic is inlined rather than held behind --verbose. This throw aborts the
    /// run, so the diagnostic is the only account of the cause — the same choice EMIT-ZERO on
    /// this path and Program.cs's non-profile EMIT-EXCLUDED branch already make (#2949).
    /// </summary>
    [Fact]
    public void TheIdentifyingAlDiagnostic_IsInTheMessage()
    {
        var msg = Message(
            new[] { "Codeunit \"DEX Dep Broken\"" }, emitted: 1,
            diagnostics: new[] { "DepBroken.Codeunit.al(5,18): error AL0185: Codeunit 'X' is missing" });

        Assert.Contains("AL0185", msg, StringComparison.Ordinal);
        Assert.Contains("DepBroken.Codeunit.al", msg, StringComparison.Ordinal);
    }

    /// <summary>
    /// Negative direction: with no diagnostics captured the message must not invent a
    /// diagnostics section promising detail that is not there. An empty "AL diagnostics:"
    /// header is the shape #2207 fixed on the bundle path — a promise with nothing behind it.
    /// </summary>
    [Fact]
    public void WithNoDiagnostics_NoEmptyDiagnosticsSectionIsPromised()
    {
        var msg = Message(new[] { "Codeunit \"DEX Dep Broken\"" }, emitted: 1);

        Assert.DoesNotContain("AL diagnostics that identified", msg, StringComparison.Ordinal);
        // ...but the substantive part is still there.
        Assert.Contains("DEX Dep Broken", msg, StringComparison.Ordinal);
    }
}

/// <summary>
/// The routing half. An EMIT-EXCLUDED dependency failure must reach Program.cs's FATAL
/// handler, and must NOT be swallowed by LoadAll's Microsoft-source-only catch on the
/// strength of the stage name alone.
///
/// That catch exists for a genuinely faithful fallback — a platform app whose procedure
/// bodies really do live in the extracted service-tier DLLs. Whether THIS app qualifies is
/// HasServiceTierDllFallback's call, unchanged by #2247; what is pinned here is that the new
/// stage does not accidentally join the METADATA-* family, which is never eligible (#3749).
/// </summary>
public sealed class DependencyEmitExclusionStageRoutingTests
{
    [Fact]
    public void EmitExcluded_IsNotAMetadataStage()
    {
        // METADATA-* is the never-swallowable family. The code path's stage is not in it, so
        // eligibility falls to the service-tier-fallback question, exactly like EMIT-FAIL,
        // EMIT-ZERO and COMPILE-FAIL beside it.
        Assert.False(DependencyLoader.IsMetadataStage("EMIT-EXCLUDED"));
        Assert.True(DependencyLoader.IsMetadataStage("METADATA-EMIT-EXCLUDED"));
    }

    [Fact]
    public void EmitExcluded_IsNeverSwallowedWithoutAFaithfulFallback()
    {
        // No service-tier index at all: nothing can answer for the dropped object, so the
        // failure must propagate to the FATAL handler rather than being deferred.
        Assert.False(DependencyLoader.IsServiceTierFallbackEligible(
            "EMIT-EXCLUDED",
            serviceTierIndexAvailable: false,
            codeunitTypeNames: new[] { "Codeunit70860", "Codeunit70861" },
            indexContains: _ => false));
    }

    [Fact]
    public void TheMetadataVariant_IsNeverSwallowedEvenWithAFullIndex()
    {
        // #3749's property, re-asserted for the new metadata stage: extracted DLLs supply
        // procedure BODIES, so they can stand in for missing code and never for missing
        // metadata. A complete index must not make this eligible.
        Assert.False(DependencyLoader.IsServiceTierFallbackEligible(
            "METADATA-EMIT-EXCLUDED",
            serviceTierIndexAvailable: true,
            codeunitTypeNames: new[] { "Codeunit70860" },
            indexContains: _ => true));
    }
}


/// <summary>
/// End-to-end through the real runner, on a dependency .app that ships two codeunits of which
/// one cannot bind. This is the test that would have caught #2247: the message tests above
/// pin what the text says; this one pins that the text is reached at all, at DEFAULT
/// verbosity, and that the run stops.
///
/// The whole fixture — the consuming bundle AND the dependency package — is built into a temp
/// directory per run rather than checked in. Three reasons, in order of weight: `*.app` is
/// gitignored, so a committed package needs `git add -f` and is then invisible to anyone
/// reading the diff; the package is a binary whose AL a reviewer could not read; and building
/// it here keeps the AL that must fail to bind, and the assertion about it, in one file.
/// The NAVX container format is the one documented in
/// tests/runner-extras/testpage-precompiled-dep-control/REGENERATE-FIXTURES.txt.
///
/// Hermetic on the compiled-deps cache: each spawn gets its own --cache root, because that
/// cache is otherwise machine-global and keyed on the package bytes, so a warm entry would
/// skip the entire compile path under test and hand back a false green — the same cache-HIT
/// hazard #3476 hit on the bundle path.
/// </summary>
public sealed class DependencyEmitExclusionEndToEndTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private const string DepAppId = "e5a6b7c8-9d0e-4f12-8a3b-4c5d6e7f8091";
    private const string DepName = "DEX Partial Source Dep";
    private const string DepPublisher = "AL Runner Fixtures";
    private const string DepVersion = "1.0.0.0";

    /// <summary>The object that binds, and must NOT be reported as dropped.</summary>
    private const string DepHealthyAl = """
        codeunit 70860 "DEX Dep Healthy"
        {
            procedure Add(A: Integer; B: Integer): Integer
            begin
                exit(A + B);
            end;
        }
        """;

    /// <summary>
    /// The object that cannot bind: it references a codeunit that exists nowhere, so BC's
    /// atomic-per-module Emit fails and the retry loop drops exactly this file and recompiles
    /// the survivor — leaving the PARTIAL assembly this test is about.
    /// </summary>
    private const string DepBrokenAl = """
        codeunit 70861 "DEX Dep Broken"
        {
            procedure Triple(A: Integer): Integer
            var
                Missing: Codeunit "DEX This Codeunit Does Not Exist At All";
            begin
                exit(Missing.Whatever(A) * 3);
            end;
        }
        """;

    /// <summary>
    /// The consuming bundle's test asserts nothing about the dependency, deliberately: the
    /// silent failure being proved is that the run looks entirely healthy — this passes, the
    /// totals are intact — while the dependency lost half its objects. A test touching the
    /// DROPPED object would fail on its own and hide the point, and one touching the
    /// SURVIVING object by name would need a SymbolReference.json the package deliberately
    /// omits (its absence is what sends LoadOne down the Tier-3 source-compile path at all).
    /// </summary>
    private const string MainTestAl = """
        codeunit 70870 "DEX Main Tests"
        {
            Subtype = Test;

            [Test]
            procedure MainBundle_RunsGreenWhileTheDependencyIsPartial()
            begin
                if 1 + 2 <> 3 then
                    Error('arithmetic must work');
            end;
        }
        """;

    private static string BuildFixture(string root)
    {
        var main = Path.Combine(root, "main");
        var packages = Path.Combine(main, ".alpackages");
        Directory.CreateDirectory(packages);

        File.WriteAllText(Path.Combine(main, "DepEmitExclusionTests.Codeunit.al"), MainTestAl);
        File.WriteAllText(Path.Combine(main, "app.json"), $$"""
            {
              "id": "f1a2b3c4-5d6e-4071-8b2c-3d4e5f607182",
              "name": "Runner Tests Fixture - Dep Emit Exclusion",
              "publisher": "AL Runner",
              "version": "1.0.0.0",
              "dependencies": [
                { "id": "{{DepAppId}}", "name": "{{DepName}}",
                  "publisher": "{{DepPublisher}}", "version": "{{DepVersion}}" }
              ],
              "idRanges": [ { "from": 70870, "to": 70879 } ],
              "runtime": "17.0",
              "target": "Cloud",
              "features": []
            }
            """);

        File.WriteAllBytes(
            Path.Combine(packages, "AL_Runner_Fixtures_DEX_Partial_Source_Dep_1.0.0.0.app"),
            BuildSourceOnlyApp());
        return main;
    }

    /// <summary>
    /// A source-only BC package: the NAVX header (magic, 40-byte header length, format
    /// version, app id as a little-endian GUID, payload length, magic again) followed by a
    /// zip of NavxManifest.xml + src/*.al. No SymbolReference.json and no .deps-bin, which is
    /// what makes DependencyLoader.LoadOne reach Tier 3 and source-compile it.
    /// </summary>
    private static byte[] BuildSourceOnlyApp()
    {
        var manifest = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/navx/2015/manifest">
              <App Id="{DepAppId}" Name="{DepName}" Publisher="{DepPublisher}" Version="{DepVersion}" ShowMyCode="true" />
              <Dependencies />
            </Package>
            """;

        using var zipBuffer = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(
                   zipBuffer, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string content)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open());
                w.Write(content);
            }
            Add("NavxManifest.xml", manifest);
            Add("src/DepHealthy.Codeunit.al", DepHealthyAl);
            Add("src/DepBroken.Codeunit.al", DepBrokenAl);
        }
        var payload = zipBuffer.ToArray();

        using var app = new MemoryStream();
        using var w2 = new BinaryWriter(app);
        w2.Write(System.Text.Encoding.ASCII.GetBytes("NAVX"));
        w2.Write(40);                                   // header length
        w2.Write(2);                                    // format version
        w2.Write(Guid.Parse(DepAppId).ToByteArray());   // little-endian, as BC writes it
        w2.Write((long)payload.Length);
        w2.Write(System.Text.Encoding.ASCII.GetBytes("NAVX"));
        w2.Write(payload);
        w2.Flush();
        return app.ToArray();
    }

    private static (string Output, int Exit) RunRunner(string bundlePath, string cacheDir)
    {
        var args = new System.Text.StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append($" --cache \"{cacheDir}\"");
        args.Append($" \"{bundlePath}\"");
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        var sb = new System.Text.StringBuilder();
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(240_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    /// <summary>
    /// The proving test. Before the fix this exact run exited 0 with "1P/0F/0E across 1 tests"
    /// and said nothing at all about the dropped object — the bundle's own test passes either
    /// way, because a green-looking run IS the defect.
    /// </summary>
    [SkippableFact]
    public void PartiallyEmittedDependency_IsLoudAtDefaultVerbosity_AndStopsTheRun()
    {
        TestArtifacts.SkipIfMissing();

        var root = Path.Combine(Path.GetTempPath(), "al-runner-dex-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var bundle = BuildFixture(root);
            var cacheDir = Path.Combine(root, "cache");
            Directory.CreateDirectory(cacheDir);

            var (output, exit) = RunRunner(bundle, cacheDir);

            Assert.True(exit != 0,
                $"a dependency that lost an object provides less than it claims, so the run "
                + $"must NOT exit 0. exit={exit}\n{output}");

            // No --verbose, and that IS the assertion: Log's component filter drops a [deps]
            // line at default verbosity (#2750), so reporting this as [deps] would reproduce
            // the original silence while looking like a fix.
            Assert.Contains("EMIT-EXCLUDED", output, StringComparison.Ordinal);

            // The dropped object by name — "something was excluded" is not actionable.
            Assert.Contains("DEX Dep Broken", output, StringComparison.Ordinal);

            // The denominator, which is what distinguishes a partial loss from a total one
            // and is the whole of why EMIT-ZERO did not already cover this.
            Assert.Contains("1 of this dependency's 2 object(s)", output, StringComparison.Ordinal);

            // The cause, at default verbosity, since this failure aborts the run.
            Assert.Contains("AL0185", output, StringComparison.Ordinal);

            // Negative direction: the SURVIVING object must not be named as dropped. A guard
            // that reports everything is as useless as one that reports nothing.
            Assert.DoesNotContain("DEX Dep Healthy", output, StringComparison.Ordinal);

            // And the partial assembly must not have been cached: the cache stores the DLL and
            // is consulted before any of this, so a written entry would make a later run skip
            // the compile, skip this guard, and be silently partial again — the same lever
            // Program.cs pulls with `cachePath = null` (#3476).
            var compiledDeps = Path.Combine(cacheDir, "compiled-deps");
            var cachedDlls = Directory.Exists(compiledDeps)
                ? Directory.GetFiles(compiledDeps, "*.dll")
                : Array.Empty<string>();
            Assert.True(cachedDlls.Length == 0,
                "a partially-emitted dependency must not be cached; a later run would load it "
                + "without recompiling and never reach the guard. found: "
                + string.Join(", ", cachedDlls.Select(Path.GetFileName)));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
