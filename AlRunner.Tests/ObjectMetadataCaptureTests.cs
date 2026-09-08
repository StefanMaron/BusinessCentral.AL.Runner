using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// #3548 step 2 — BC's own emitter hands
/// <c>CaptureOutputter.AddApplicationObject</c> a metadata document for every
/// application object it emits, and the runner used to keep three kinds (report,
/// page, xmlport) and drop the rest. <see cref="AlObjectMetadataRegistry"/> keeps all
/// of them, keyed by (kind, id).
///
/// Two claims, and the second is the one the issue says will sink this work if it is
/// missed: BC's Emit runs only on a compile-cache MISS, so a registry fed from there
/// alone is empty on every warm run and every consumer silently takes its not-found
/// branch. The warm assertions below are identical to the cold ones and run against
/// the same cache directory, per .claude/rules/local-test-scope.md.
///
/// Observed through <c>AL_RUNNER_TRACE_OBJECT_METADATA=1</c>'s per-entry line rather
/// than through AL, because this PR deliberately converts no consumer — nothing reads
/// the registry yet, so there is no AL-observable behaviour to assert on. The trace
/// line is emitted by Register itself, which is the single funnel both the emit path
/// and the sidecar-replay path go through.
///
/// Spawns the real runner; needs the BC artifact cache. Skips when absent.
/// </summary>
public class ObjectMetadataCaptureTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");
    private static readonly string FixtureRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "ObjectMetadataCapture"));

    /// <summary>
    /// The (kind, id) pairs the fixture declares, as the trace spells them. Table,
    /// page, codeunit, enum, report, xmlport and query all share id 70660 on purpose —
    /// a registry keyed on the id alone answers seven of these with one document, and
    /// only a per-kind assertion notices.
    ///
    /// Query is here and Interface is not, which is the measurement rather than a
    /// guess: see docs/object-metadata-capture.md#which-kinds-arrive.
    /// </summary>
    private static readonly (string Kind, int Id)[] Expected =
    {
        ("Table", 70660),
        ("Page", 70660),
        ("Codeunit", 70660),
        ("Enum", 70660),
        ("Report", 70660),
        ("XmlPort", 70660),
        ("Query", 70660),
        ("PermissionSet", 70661),
        ("TableExtension", 70662),
        ("PageExtension", 70663),
        ("EnumExtension", 70664),
        ("PermissionSetExtension", 70665),
        ("ReportExtension", 70666),
    };

    private static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src))
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), overwrite: true);
    }

    private static (string output, int exit) RunRunner(string bundleDir, string alCacheDir)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append($" \"{bundleDir}\"");
        args.Append($" --cache \"{alCacheDir}\"");
        args.Append(" --verbose");
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = args.ToString(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoRoot,
        };
        psi.Environment["AL_RUNNER_TRACE_OBJECT_METADATA"] = "1";
        var platformApps = TestArtifacts.PlatformAppsDir();
        if (Directory.Exists(platformApps))
            psi.Arguments += $" --package-cache \"{platformApps}\"";
        var sb = new StringBuilder();
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(300_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    private static void AssertEveryKindCaptured(string output, string phase)
    {
        foreach (var (kind, id) in Expected)
        {
            Assert.True(
                output.Contains($"[object-metadata] registered {kind} {id} "),
                $"{phase}: no metadata document captured for {kind} {id}. "
                + "BC's emitter produces one for every object it emits; a missing kind means "
                + $"AddApplicationObject dropped it.\n{output}");
        }

        // Negative direction, and the reason the fixture reuses one id across six kinds:
        // 70660 is a table, a page, a codeunit, an enum, a report and an xmlport, and is
        // NOT a tableextension. A registry keyed on the id alone — or one that answered
        // any kind for a known id — passes every positive assertion above and fails here.
        Assert.DoesNotContain("[object-metadata] registered TableExtension 70660 ", output);
        // Nothing in the fixture declares 70670; a capture that registered a document
        // under an id it invented would show up here.
        Assert.DoesNotContain(" 70670 ", output);
    }

    [SkippableFact]
    public void EveryEmittedObjectKind_IsCaptured_ColdAndOnAWarmCacheHit()
    {
        TestArtifacts.SkipIfMissing();

        var scratch = TestScratch.Dir("al-runner-object-metadata-capture");
        var bundle = Path.Combine(scratch, "bundle");
        var alCacheDir = Path.Combine(scratch, "al-out");
        CopyDir(FixtureRoot, bundle);

        // Run 1 — cold. The AL-output cache is empty, so BC's Emit runs and
        // CaptureOutputter sees every object.
        var (cold, coldExit) = RunRunner(bundle, alCacheDir);
        Assert.True(coldExit == 0 && cold.Contains("1P/0F/0E"), $"cold run must pass:\n{cold}");
        AssertEveryKindCaptured(cold, "cold run");

        // Run 2 — same sources, same cache directory, so the AL-output cache HITs and
        // Emit never runs. Without a replayed sidecar the registry is empty here and
        // every assertion below fails while the run itself stays green: exactly the
        // failure the source-text parser in RecordPatches.AlObjectDeclParser.cs was
        // written to avoid.
        var (warm, warmExit) = RunRunner(bundle, alCacheDir);
        Assert.True(warmExit == 0 && warm.Contains("1P/0F/0E"), $"warm run must pass:\n{warm}");
        Assert.Contains("[cache] HIT", warm);
        AssertEveryKindCaptured(warm, "warm run (AL-output cache HIT)");
    }

    /// <summary>
    /// The third replay path: a source-compiled DEPENDENCY served from the
    /// `compiled-deps` cache skips its own Emit, so its objects are captured only if
    /// the dependency's own `.object-metadata.json` sidecar is written and replayed.
    /// Same two-process "dep HIT + bundle MISS" sequence as
    /// <see cref="SourceDepCacheEnumMetadataTests"/>, which is where that shape and its
    /// fresh-AppId reasoning come from.
    /// </summary>
    [SkippableFact]
    public void DependencyObjects_AreCaptured_AcrossASourceDepCacheHit()
    {
        TestArtifacts.SkipIfMissing();

        var scratch = TestScratch.Dir("al-runner-object-metadata-dep");
        var depDir = Path.Combine(scratch, "dep-app");
        var testsDir = Path.Combine(scratch, "tests-app");
        var alCacheDir = Path.Combine(scratch, "al-out");
        Directory.CreateDirectory(depDir);
        Directory.CreateDirectory(testsDir);

        // Fresh identities: the dep's Tier-3 cache key has never been seen, so run 1 is
        // unconditionally a dep MISS and run 2 an unconditional dep HIT.
        var depId = Guid.NewGuid();
        var testsId = Guid.NewGuid();

        File.WriteAllText(Path.Combine(depDir, "app.json"), $$"""
        {
          "id": "{{depId}}",
          "name": "OMR Dep App",
          "publisher": "OMR",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 70670, "to": 70674 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(depDir, "Dep.al"), """
        table 70670 "OMR Dep Thing"
        {
            DataClassification = CustomerContent;
            fields { field(1; "Entry No."; Integer) { DataClassification = CustomerContent; } }
            keys { key(PK; "Entry No.") { Clustered = true; } }
        }

        codeunit 70670 "OMR Dep Service"
        {
            procedure Answer(): Integer
            begin
                exit(7);
            end;
        }

        enum 70670 "OMR Dep Kind"
        {
            Extensible = true;
            value(0; Plain) { Caption = 'Plain'; }
        }
        """);

        File.WriteAllText(Path.Combine(testsDir, "app.json"), $$"""
        {
          "id": "{{testsId}}",
          "name": "OMR Dep Tests",
          "publisher": "OMR",
          "version": "1.0.0.0",
          "dependencies": [
            { "id": "{{depId}}", "name": "OMR Dep App", "publisher": "OMR", "version": "1.0.0.0" }
          ],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 70675, "to": 70679 } ],
          "runtime": "14.0"
        }
        """);
        var testsAlPath = Path.Combine(testsDir, "Tests.al");
        File.WriteAllText(testsAlPath, """
        codeunit 70675 "OMR Dep Tests"
        {
            Subtype = Test;

            [Test]
            procedure DepAnswers()
            var
                Service: Codeunit "OMR Dep Service";
            begin
                if Service.Answer() <> 7 then
                    Error('Expected 7, got %1', Service.Answer());
            end;
        }
        """);

        (string Kind, int Id)[] depObjects =
        {
            ("Table", 70670), ("Codeunit", 70670), ("Enum", 70670),
        };

        var (run1, exit1) = RunRunner(testsDir, alCacheDir);
        Assert.True(exit1 == 0 && run1.Contains("1P/0F/0E"), $"run 1 (all cold) must pass:\n{run1}");
        foreach (var (kind, id) in depObjects)
            Assert.True(run1.Contains($"[object-metadata] registered {kind} {id} "),
                $"run 1: dependency object {kind} {id} was not captured.\n{run1}");

        // Touch the tests bundle only: its cache key changes (bundle MISS) while the
        // dep's synthesized .app stays byte-identical (dep HIT).
        File.AppendAllText(testsAlPath, "\n// touched\n");

        var (run2, exit2) = RunRunner(testsDir, alCacheDir);
        Assert.True(exit2 == 0 && run2.Contains("1P/0F/0E"), $"run 2 (dep HIT) must pass:\n{run2}");
        Assert.Contains("source-cache HIT", run2);
        foreach (var (kind, id) in depObjects)
            Assert.True(run2.Contains($"[object-metadata] registered {kind} {id} "),
                $"run 2: dependency object {kind} {id} lost its metadata when the dependency "
                + $"was served from the compiled-deps cache.\n{run2}");
    }
}
