// CoverageMultiObjectFileTests — #3713: --coverage reported only the FIRST object of a
// multi-object .al file; lines executed in a later object were absent from the report,
// indistinguishable from never executed. Exit 0, no warning.
//
// The loss is in AlCoverageSourceMap.Build, the (object label, object id) -> file map that
// both writers consume: it registered one header per file (`Regex.Match`, not `Matches`), on
// the belief that "AL files declare exactly one top-level object". They do not — alc compiles
// a file holding several objects at 0 errors, and real apps do it (1 of 553 files in Continia
// Document Output). Every object after the first had no file, so AlCoverageTracker.Collect
// skipped its scopes, and the Cobertura class for the file carried the first object's lines
// only. Server mode (perTestCoverage) shares the map and lost the file altogether.
//
// Two layers: the map itself, in-process and fast; and the Cobertura output of a real run,
// asserting the SPECIFIC line of the second object with a SPECIFIC hit count, so a fix that
// merely stopped crashing, or reported every line as 0, cannot pass.

using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

// The map facts parse AL with BC's own parser in-process, so they join BcEngineCollection
// (see that file); the end-to-end fact spawns the runner and needs only the artifact cache.
[Collection(BcEngineCollection.Name)]
public sealed class CoverageMultiObjectFileTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private readonly BcEngineFixture _engine;
    private readonly string _root;

    public CoverageMultiObjectFileTests(BcEngineFixture engine)
    {
        _engine = engine;
        _root = TestScratch.Dir("al-runner-coverage-multi-object");
        Directory.CreateDirectory(_root);
    }

    private void RequireEngine() =>
        TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ── the map ──────────────────────────────────────────────────────────────────────────

    [SkippableFact]
    public void Build_FileWithTwoCodeunits_MapsBothIdsToTheFile()
    {
        RequireEngine();
        File.WriteAllText(Path.Combine(_root, "Two.Codeunit.al"), """
        codeunit 63600 "Cov First"
        {
            procedure FirstOnly(): Integer
            begin
                exit(11);
            end;
        }

        codeunit 63601 "Cov Second"
        {
            procedure SecondOnly(): Integer
            begin
                exit(22);
            end;
        }
        """);

        var map = AlCoverageSourceMap.Build(new[] { _root }, relativeTo: _root);

        Assert.Equal("Two.Codeunit.al", map[("CodeUnit", 63600)]);
        Assert.Equal("Two.Codeunit.al", map[("CodeUnit", 63601)]);
        Assert.Equal(2, map.Count);
    }

    /// <summary>
    /// The second object's text begins on file line 8 (0-based 7): the blank line after the
    /// first object's closing brace is its leading trivia, and BC's [SourceSpans] count from
    /// there. That is the offset every consumer adds to a decoded statement line.
    /// </summary>
    [SkippableFact]
    public void Build_SecondObjectInAFile_CarriesItsStartLineAsOffset()
    {
        RequireEngine();
        File.WriteAllText(Path.Combine(_root, "Two.Codeunit.al"), """
        codeunit 63600 "Cov First"
        {
            procedure FirstOnly(): Integer
            begin
                exit(11);
            end;
        }

        codeunit 63601 "Cov Second"
        {
            procedure SecondOnly(): Integer
            begin
                exit(22);
            end;
        }
        """);

        var map = AlCoverageSourceMap.Build(new[] { _root }, relativeTo: _root);

        Assert.Equal(0, map.LineOffset("CodeUnit", 63600));
        Assert.Equal(7, map.LineOffset("CodeUnit", 63601));
        Assert.Equal(0, map.LineOffset("CodeUnit", 99999)); // unknown object: no offset, never a throw
    }

    [SkippableFact]
    public void Build_FileWithATableAndACodeunit_MapsBothKinds()
    {
        RequireEngine();
        File.WriteAllText(Path.Combine(_root, "Mixed.al"), """
        table 63602 "Cov Mixed Table"
        {
            fields { field(1; "Code"; Code[20]) { } }
            keys { key(PK; "Code") { Clustered = true; } }
        }

        codeunit 63603 "Cov Mixed Codeunit"
        {
            procedure P(): Integer
            begin
                exit(1);
            end;
        }
        """);

        var map = AlCoverageSourceMap.Build(new[] { _root }, relativeTo: _root);

        Assert.Equal("Mixed.al", map[("Table", 63602)]);
        Assert.Equal("Mixed.al", map[("CodeUnit", 63603)]);
    }

    /// <summary>Pin for the one-object file, green before and after: exactly one entry.</summary>
    [SkippableFact]
    public void Build_FileWithOneCodeunit_MapsExactlyOneId()
    {
        RequireEngine();
        File.WriteAllText(Path.Combine(_root, "One.Codeunit.al"), """
        codeunit 63604 "Cov One"
        {
            procedure P(): Integer
            begin
                // codeunit 63605 mentioned in a comment is not a declaration
                exit(1);
            end;
        }
        """);

        var map = AlCoverageSourceMap.Build(new[] { _root }, relativeTo: _root);

        Assert.Equal(new[] { ("CodeUnit", 63604) }, map.Keys.ToArray());
    }

    // ── the report, end to end ───────────────────────────────────────────────────────────

    private void WriteBundle(string dir)
    {
        Directory.CreateDirectory(dir);
        // No "application" property — see .claude/rules/no-base-app-in-csharp-tests.md.
        File.WriteAllText(Path.Combine(dir, "app.json"), """
        {
          "id": "5a3f0b11-3713-4a01-8001-000000003713",
          "name": "CMOF Coverage Probe",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 63600, "to": 63619 } ],
          "runtime": "14.0"
        }
        """);
        // Line numbers below are load-bearing: `exit(11)` is file line 5 (first object, never
        // called) and `exit(22)` is file line 13 (second object, called by the test).
        File.WriteAllText(Path.Combine(dir, "Two.Codeunit.al"), """
        codeunit 63600 "CMOF First"
        {
            procedure FirstOnly(): Integer
            begin
                exit(11);
            end;
        }

        codeunit 63601 "CMOF Second"
        {
            procedure SecondOnly(): Integer
            begin
                exit(22);
            end;
        }
        """);
        File.WriteAllText(Path.Combine(dir, "T.Codeunit.al"), """
        codeunit 63610 "CMOF Tests"
        {
            Subtype = Test;

            [Test]
            procedure CallsOnlySecond()
            var
                S: Codeunit "CMOF Second";
            begin
                if S.SecondOnly() <> 22 then
                    Error('expected 22');
            end;
        }
        """);
    }

    private (string Output, int Exit) Spawn(string bundle, params string[] extraArgs)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append(" --no-cache"); // must re-emit and re-instrument, not replay an al-out cache HIT
        foreach (var a in extraArgs) args.Append(' ').Append(a);
        args.Append(" \"").Append(bundle).Append('"');
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        var sb = new StringBuilder();
        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(600_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    /// <summary>The Cobertura class whose filename ends with <paramref name="fileName"/>.</summary>
    private static XElement ClassFor(XDocument doc, string fileName)
    {
        var cls = doc.Descendants("class").FirstOrDefault(c =>
            (c.Attribute("filename")?.Value ?? "").Replace('\\', '/').EndsWith("/" + fileName, StringComparison.Ordinal));
        Assert.True(cls != null, $"no <class> for {fileName} in:\n{doc}");
        return cls!;
    }

    private static Dictionary<int, int> LinesOf(XElement cls) =>
        cls.Descendants("line").ToDictionary(
            l => int.Parse(l.Attribute("number")!.Value),
            l => int.Parse(l.Attribute("hits")!.Value));

    /// <summary>
    /// RED before the fix: the class for Two.Codeunit.al held line 5 only (hits 0); line 13,
    /// which the passing test demonstrably executed, was absent, and lines-valid was 3.
    /// </summary>
    [SkippableFact]
    public void Coverage_SecondObjectInAFile_IsReportedWithItsHits()
    {
        TestArtifacts.SkipIfMissing();
        var bundle = Path.Combine(_root, "bundle");
        WriteBundle(bundle);
        var coveragePath = Path.Combine(_root, "cobertura.xml");

        var (output, exit) = Spawn(bundle, "--coverage", $"--coverage-out \"{coveragePath}\"");

        Assert.Equal(0, exit);
        Assert.Contains("CallsOnlySecond", output);
        Assert.True(File.Exists(coveragePath), $"cobertura.xml was not written.\n{output}");
        var doc = XDocument.Load(coveragePath);

        var lines = LinesOf(ClassFor(doc, "Two.Codeunit.al"));
        Assert.Equal(new[] { 5, 13 }, lines.Keys.OrderBy(k => k).ToArray());
        Assert.Equal(0, lines[5]);   // FirstOnly, never called: still reported, as 0
        Assert.Equal(1, lines[13]);  // SecondOnly, called once by the test

        // The whole-report totals count the second object too: 2 lines here + 2 in T.Codeunit.al.
        var cov = doc.Root!;
        Assert.Equal("4", cov.Attribute("lines-valid")!.Value);
        Assert.Equal("2", cov.Attribute("lines-covered")!.Value);
    }
}
