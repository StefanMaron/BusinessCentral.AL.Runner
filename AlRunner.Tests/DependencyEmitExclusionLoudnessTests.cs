// DependencyEmitExclusionLoudnessTests — #2247: a dependency that emits only SOME of its
// objects must say so, rather than being loaded partially in silence.
//
// CLAIM: DependencyLoader.LoadOne called the same BcCompiler.Emit the bundled path calls and
// read only `.Sources`, so an emit-retry exclusion was loaded, cached and reported as a clean
// success, at every verbosity.
//
// CITATION: measured on the fixture this file generates — exit 0, "1P/0F/0E across 1 tests",
// and `grep -c` = 0 over the whole run log for the dropped object, EMIT-EXCLUDED and AL0185.
// Confirmed off disk rather than from a missing log line: the cached dependency DLL held
// Codeunit70860 and not Codeunit70861, and its .object-metadata.json listed 1 object where the
// package shipped 2. #3875 measured the same shape on the metadata path (55 documents of 70).
//
// TRAP, for whoever edits this next: the fix REPORTS and continues; it does not fail the run.
// Refusing was measured and is wrong — it aborts the entire runner-extras suite, because
// Microsoft's Tests-TestLibraries drops 1 of 203 objects on a DotNet type that is unavailable
// headless. DependencyLoader.cs's guard carries that reasoning at the line.

using AlRunner;
using AlRunner.Infrastructure;
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
    /// The AL diagnostic is inlined rather than held behind --verbose: it is the only account
    /// of why the object was dropped, and the run continues past it (#2949).
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
/// The stage-naming half. METADATA-EMIT-EXCLUDED must land in the never-swallowable METADATA-*
/// family and the code-path stage must not (#3749) — IsMetadataStage matches on the prefix, so
/// a misnamed stage would silently become swallowable.
/// </summary>
public sealed class DependencyEmitExclusionStageRoutingTests
{
    [Fact]
    public void EmitExcluded_IsNotAMetadataStage()
    {
        // METADATA-* is the never-swallowable family; the code-path stage is not in it.
        Assert.False(DependencyLoader.IsMetadataStage("EMIT-EXCLUDED"));
        Assert.True(DependencyLoader.IsMetadataStage("METADATA-EMIT-EXCLUDED"));
    }

    [Fact]
    public void EmitExcluded_IsNeverSwallowedWithoutAFaithfulFallback()
    {
        // No service-tier index: nothing can answer for the dropped object, so a
        // METADATA-EMIT-EXCLUDED must propagate rather than be deferred.
        Assert.False(DependencyLoader.IsServiceTierFallbackEligible(
            "EMIT-EXCLUDED",
            serviceTierIndexAvailable: false,
            codeunitTypeNames: new[] { "Codeunit70860", "Codeunit70861" },
            indexContains: _ => false));
    }

    [Fact]
    public void TheMetadataVariant_IsNeverSwallowedEvenWithAFullIndex()
    {
        // #3749: extracted DLLs supply procedure BODIES, so they can stand in for missing code
        // and never for missing metadata. A complete index must not make this eligible.
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
    /// and said NOTHING about the dropped object at any verbosity — the bundle's own test
    /// passes either way, because a green-looking run IS the defect.
    ///
    /// The run still succeeds after the fix, deliberately (see the guard's own comment): what
    /// changed is that the loss is stated at the point of discovery AND in the run summary,
    /// where a reader of a long log will actually find it.
    /// </summary>
    [SkippableFact]
    public void PartiallyEmittedDependency_IsReportedAtDefaultVerbosity_AndInTheRunSummary()
    {
        TestArtifacts.SkipIfMissing();

        var root = TestScratch.FlatDir("al-runner-dex-");
        Directory.CreateDirectory(root);
        try
        {
            var bundle = BuildFixture(root);
            var cacheDir = Path.Combine(root, "cache");
            Directory.CreateDirectory(cacheDir);

            var (output, exit) = RunRunner(bundle, cacheDir);

            // No --verbose, and that IS the assertion: Log's component filter drops a [deps]
            // line at default verbosity (#2750), so reporting this as [deps] would reproduce
            // the original silence while looking like a fix.
            Assert.Contains("EMIT-EXCLUDED", output, StringComparison.Ordinal);

            // The dropped object by name — "something was excluded" is not actionable.
            Assert.Contains("DEX Dep Broken", output, StringComparison.Ordinal);

            // The denominator, which is what distinguishes a partial loss from a total one and
            // is the whole of why the EMIT-ZERO guard beside it did not already cover this.
            Assert.Contains("1 of this dependency's 2 object(s)", output, StringComparison.Ordinal);

            // The cause, at default verbosity.
            Assert.Contains("AL0185", output, StringComparison.Ordinal);

            // Reported in the SUMMARY too, not only at the point of discovery: this run
            // continues, so on a real run the discovery line scrolls thousands of lines above
            // the part anyone reads (#2587). This is the assertion that would fail if the
            // report were downgraded to a bare stderr write.
            Assert.Contains("Provisioning gaps:", output, StringComparison.Ordinal);

            // Negative direction: the SURVIVING object must not be named as dropped. A guard
            // that reports everything is as useless as one that reports nothing.
            Assert.DoesNotContain("DEX Dep Healthy", output, StringComparison.Ordinal);

            // And the run still completes. Failing the load instead was measured and is wrong:
            // it aborts the whole runner-extras suite over Microsoft's Tests-TestLibraries
            // dropping one of 203 objects on a headless-unavailable DotNet type.
            Assert.Equal(0, exit);
            Assert.Contains("MainBundle_RunsGreenWhileTheDependencyIsPartial", output, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}

/// <summary>
/// The METADATA path's own RED → GREEN (#2247, second half). The two facts above assert the
/// stage NAMING convention, which would pass with the guard deleted — this drives the guard.
///
/// CLAIM: DependencyMetadataProducer.Ensure discarded the BcEmitOutput entirely
/// (`compiler.Emit(...)` with no assignment) and checked only `produced.Length == 0`, so a
/// partial emit was persisted as the app's COMPLETE metadata. It cannot see the shortfall by
/// counting — it knows how many documents appeared, never how many BC should have produced.
///
/// CITATION: #3875 measured Business Foundation at 55 documents of 70, reported as success with
/// zero AL0185 lines at default verbosity.
///
/// TRAP: unlike the LoadOne path, which reports and continues, this one THROWS — because Persist
/// writes a sidecar every later run replays without recompiling, so a partial document set would
/// become this app's recorded metadata permanently.
/// </summary>
public sealed class DependencyMetadataPartialEmitTests
{
    /// <summary>
    /// A source-shipping package with TWO tables, one of which cannot bind: its field carries a
    /// TableRelation to a table that exists nowhere, so BC's atomic-per-module Emit fails and
    /// the retry loop drops exactly that one and recompiles the survivor — the partial emit.
    /// </summary>
    private static string WritePartialSourcePackage(Guid appId)
    {
        var path = TestScratch.FilePath("depmeta-partial", "pkg-partial-source.app");
        using var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(
            ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string content)
            {
                using var s = zip.CreateEntry(name).Open();
                s.Write(System.Text.Encoding.UTF8.GetBytes(content));
            }
            Add("NavxManifest.xml",
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
                + "<Package xmlns=\"http://schemas.microsoft.com/navx/2015/manifest\">"
                + $"<App Id=\"{appId}\" Name=\"DEX Partial Metadata Dep\" Publisher=\"AL Runner Fixtures\""
                + " Version=\"1.0.0.0\" ShowMyCode=\"true\" /><Dependencies /></Package>");
            Add("src/Healthy.Table.al",
                "table 70880 \"DEX Meta Healthy\"\n"
                + "{\n    DataClassification = CustomerContent;\n"
                + "    fields { field(1; \"No.\"; Code[20]) { } }\n"
                + "    keys { key(PK; \"No.\") { Clustered = true; } }\n}\n");
            Add("src/Broken.Codeunit.al",
                "codeunit 70881 \"DEX Meta Broken\"\n"
                + "{\n    procedure Go()\n    var\n"
                + "        Missing: Codeunit \"DEX Meta No Such Codeunit Anywhere\";\n"
                + "    begin\n        Missing.Whatever();\n    end;\n}\n");
        }
        var zipBytes = ms.ToArray();
        var result = new byte[8 + zipBytes.Length];
        result[0] = (byte)'N'; result[1] = (byte)'A'; result[2] = (byte)'V'; result[3] = (byte)'X';
        BitConverter.TryWriteBytes(result.AsSpan(4, 4), (uint)8);
        zipBytes.CopyTo(result, 8);
        File.WriteAllBytes(path, result);
        return path;
    }

    /// <summary>
    /// The proving test. With the guard removed this returns 1 — one document, from the table
    /// that survived — and persists it as the app's complete metadata. It must throw instead,
    /// naming the dropped object and the denominator.
    /// </summary>
    [SkippableFact]
    public void PartialMetadataEmit_ThrowsRatherThanCachingAnIncompleteDocumentSet()
    {
        TestArtifacts.SkipIfMissing();

        // A FRESH app id per run, not a constant: the metadata sidecar is keyed on it and
        // lives in a machine-global cache, so a constant id lets one run's entry satisfy the
        // next run's cache HIT before Ensure ever compiles. That is not hypothetical — it
        // happened while proving this test: the mutated (unguarded) build cached 1 document
        // for a 2-object package, and the restored build then read that entry back and
        // returned early, so the test stayed red with the guard present. The poisoned cache
        // IS the defect this guard exists to prevent; the test must not depend on it.
        var appId = Guid.NewGuid();
        var pkg = WritePartialSourcePackage(appId);
        var manifest = new AppManifest(
            Publisher: "AL Runner Fixtures", Name: "DEX Partial Metadata Dep",
            Version: new Version(1, 0, 0, 0), AppId: appId,
            Dependencies: Array.Empty<DependencyRef>());

        var ex = Assert.Throws<DependencyLoadException>(
            () => DependencyMetadataProducer.Ensure(manifest, pkg, new BcCompiler()));

        // The stage, which is what keeps it in the never-swallowable METADATA-* family (#3749).
        Assert.Equal("METADATA-EMIT-EXCLUDED", ex.Stage);
        // The dropped object by name, and the denominator that makes a partial loss legible.
        Assert.Contains("DEX Meta Broken", ex.Message, StringComparison.Ordinal);
        Assert.Contains("1 of this dependency's 2 object(s)", ex.Message, StringComparison.Ordinal);
        // And why throwing is the right answer HERE specifically, where LoadOne continues.
        Assert.Contains("metadata document(s) were produced", ex.Message, StringComparison.Ordinal);

        // Negative direction, and the half that makes the throw worth having: nothing was
        // persisted. A cached partial set is replayed by every later run without recompiling,
        // so it would record 1 document as this app's complete metadata permanently.
        var sidecar = Path.Combine(
            AlRunner.Infrastructure.CacheRoots.Resolve("dep-metadata"),
            DependencyMetadataProducer.CacheKey(manifest) + ".object-metadata.json");
        Assert.False(File.Exists(sidecar),
            $"a partial metadata emit must not be cached; found {sidecar}");
    }
}
