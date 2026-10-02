// CoverageSiblingSourceBesideExecutionRootTests — #5222: when a registered source folder (a sibling
// source project, matched by app id) declares an object that an EXECUTION root also declares, the
// report named the sibling's file and used the sibling's line offset for it, while the statement
// lines came from the text the execution root compiled. Every object after a file's first sat at
// (sibling's offset) + (compiled line - compiled offset): shifted, and able to land inside an
// earlier object. The first object of a file carries offset 0 in both, so only its LABEL was wrong.
//
// AlCoverageSourceMap.RootsWithParsedSourceDependencies promised that the sibling roots "only add
// files that had no root at all"; Build kept the LAST root's mapping instead, so a sibling replaced
// the execution root's. docs/coverage-attribution.md#a-sibling-source-folder-beside-an-execution-root-5222
//
// Two layers: the map in-process, and the issue's own layout through the CLI (which is also what
// proves RootsWithParsedSourceDependencies hands Build the execution roots), run twice against one
// cache root so the warm run is read as well as the cold one.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class CoverageSiblingSourceBesideExecutionRootTests : IDisposable
{
    private const int SpawnTimeoutMs = 240_000;
    private static readonly string ProjectPath = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "AlRunner"));
    private static readonly Guid AppId = Guid.Parse("6c779974-6ae1-42b9-9842-ca25979bae35");
    private static readonly Guid TestAppId = Guid.Parse("3f1c8d52-7b04-4e9a-a6d3-9e2b5c7a1d52");

    // The original source: Multi A is short, so Multi B starts early in the file.
    private const string SourceText = """
        codeunit 79800 "Multi A"
        {
            procedure Never(X: Integer): Integer
            begin
                exit(X + 7);
            end;
        }

        codeunit 79801 "Multi B"
        {
            procedure Reached(X: Integer): Integer
            begin
                if X > 10 then
                    exit(X + 1);
                exit(X);
            end;

            procedure Unreached(X: Integer): Integer
            begin
                exit(X * 3);
            end;
        }
        """;

    // What is compiled: the same two codeunits with extra statements, so Multi A is longer and
    // every line of Multi B sits further down than it does in SourceText.
    private const string BundleText = """
        codeunit 79800 "Multi A"
        {
            procedure Never(X: Integer): Integer
            var
                Y: Integer;
            begin
                Y := Y + 1;
                Y := Y + 2;
                Y := Y + 3;
                Y := Y + 4;
                Y := Y + 5;
                Y := Y + 6;
                Y := Y + 7;
                Y := Y + 8;
                exit(X + 7);
            end;
        }

        codeunit 79801 "Multi B"
        {
            procedure Reached(X: Integer): Integer
            var
                Z: Integer;
            begin
                Z := 1;
                if X > 10 then
                    exit(X + 1);
                exit(X);
            end;

            procedure Unreached(X: Integer): Integer
            begin
                exit(X * 3);
            end;
        }
        """;

    private const string OnlyInSourceText = """
        codeunit 79802 "Multi Only In Source"
        {
            procedure P()
            begin
            end;
        }
        """;

    private const string TestsText = """
        codeunit 79811 "Multi Tests"
        {
            Subtype = Test;

            [Test]
            procedure CallsB()
            var
                B: Codeunit "Multi B";
            begin
                if B.Reached(20) <> 21 then
                    Error('Reached(20) returned %1', B.Reached(20));
                if B.Reached(5) <> 5 then
                    Error('Reached(5) returned %1', B.Reached(5));
            end;
        }
        """;

    private readonly BcEngineFixture _engine;
    private readonly string _root = TestScratch.Dir("al-runner-coverage-sibling-beside-execution-root");

    public CoverageSiblingSourceBesideExecutionRootTests(BcEngineFixture engine) => _engine = engine;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static string Abs(string p) => Path.GetFullPath(p).Replace('\\', '/');

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static void WriteManifest(string dir, Guid id, string name, bool dependsOnApp)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), JsonSerializer.Serialize(new
        {
            id,
            name,
            publisher = "AL Runner",
            version = "1.0.0.0",
            runtime = "14.0",
            idRanges = new[] { new { from = 79800, to = 79830 } },
            dependencies = dependsOnApp
                ? new[] { new { id = AppId, name = "Multi", publisher = "AL Runner", version = "1.0.0.0" } }
                : Array.Empty<object>(),
        }));
    }

    /// <summary>bundle/ (what runs) and src/ (the same app's original source, same app id), side by side.</summary>
    private (string Bundle, string Source) Layout()
    {
        var bundle = Path.Combine(_root, "bundle");
        var source = Path.Combine(_root, "src");
        WriteManifest(bundle, AppId, "Multi", dependsOnApp: false);
        WriteManifest(source, AppId, "Multi", dependsOnApp: false);
        Write(Path.Combine(bundle, "MultiPair.Codeunit.al"), BundleText);
        Write(Path.Combine(source, "MultiPair.Codeunit.al"), SourceText);
        return (bundle, source);
    }

    private AlSourceLocationMap BuildMap(string[] roots, string[]? executionRoots)
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");
        return AlCoverageSourceMap.Build(
            new CoverageRoots(roots, new Dictionary<string, string>(StringComparer.Ordinal), executionRoots),
            relativeTo: null);
    }

    [SkippableFact]
    public void ASiblingFolder_DoesNotReplace_WhatAnExecutionRootMapped_ForEveryObjectOfTheFile()
    {
        var (bundle, source) = Layout();
        var bundleFile = Abs(Path.Combine(bundle, "MultiPair.Codeunit.al"));

        var map = BuildMap(new[] { bundle, source }, new[] { bundle });

        Assert.Equal(bundleFile, map[("CodeUnit", 79800)]);
        Assert.Equal(bundleFile, map[("CodeUnit", 79801)]);

        // The offset is the compiled file's own, measured by mapping the bundle alone; the
        // source folder's would be smaller by the difference in Multi A's length.
        var alone = BuildMap(new[] { bundle }, new[] { bundle });
        var sourceAlone = BuildMap(new[] { source }, null);
        Assert.NotEqual(sourceAlone.LineOffset("CodeUnit", 79801), alone.LineOffset("CodeUnit", 79801));
        Assert.Equal(alone.LineOffset("CodeUnit", 79801), map.LineOffset("CodeUnit", 79801));
        Assert.Equal(alone.LineOffset("CodeUnit", 79800), map.LineOffset("CodeUnit", 79800));

        // The text a decoded line indexes is the compiled text, not the source folder's.
        Assert.Equal(alone.ObjectSourceLines("CodeUnit", 79801), map.ObjectSourceLines("CodeUnit", 79801));
    }

    // The other direction: the sibling is not blanket-ignored. An object no execution root declares
    // still comes from it (#3965), or a sibling source dependency's executed lines vanish again.
    [SkippableFact]
    public void ASiblingFolder_StillSuppliesAnObject_NoExecutionRootDeclares()
    {
        var (bundle, source) = Layout();
        Write(Path.Combine(source, "OnlyInSource.Codeunit.al"), OnlyInSourceText);

        var map = BuildMap(new[] { bundle, source }, new[] { bundle });

        Assert.Equal(Abs(Path.Combine(source, "OnlyInSource.Codeunit.al")), map[("CodeUnit", 79802)]);
        Assert.Equal(Abs(Path.Combine(bundle, "MultiPair.Codeunit.al")), map[("CodeUnit", 79801)]);
    }

    private (string Output, int Exit) SpawnRunner(params string[] args)
    {
        var sb0 = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        foreach (var a in args) sb0.Append(" \"").Append(a).Append('"');
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = sb0.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = _root,
        };
        var sb = new StringBuilder();
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        Assert.True(p.WaitForExit(SpawnTimeoutMs), $"runner did not exit within {SpawnTimeoutMs / 1000}s");
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    // The issue's layout: a test calling only Multi B.Reached; bundle/ compiled and run, src/ beside
    // it with the same app id. Every line the report marks as executed must hold a statement of
    // Multi B.Reached in the FILE THE REPORT NAMES, and that file is the compiled one.
    [SkippableFact]
    public void Cli_Coverage_ReportsTheCompiledFile_AndItsLines_ForTheSecondObject_ColdAndWarm()
    {
        TestArtifacts.SkipIfMissing();
        var (bundle, _) = Layout();
        var testApp = Path.Combine(_root, "tests");
        WriteManifest(testApp, TestAppId, "Multi Tests", dependsOnApp: true);
        Write(Path.Combine(testApp, "src", "MultiTests.Codeunit.al"), TestsText);
        var cache = Path.Combine(_root, "cache");
        var executed = new[] { "Z := 1;", "if X > 10 then", "exit(X + 1);", "exit(X);" };

        foreach (var run in new[] { "cold", "warm" })
        {
            var covPath = Path.Combine(_root, $"coverage-{run}.xml");
            var args = new List<string> { "--isolation", "test", "--cache", cache, "--coverage", "--coverage-out", covPath };
            args.AddRange(TestBuildConfig.BcVersionArg.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            args.AddRange(new[] { "bundle", "tests" });
            var (output, exit) = SpawnRunner(args.ToArray());
            Assert.True(exit == 0, $"{run}: the one test must pass, exit was {exit}.\n{output}");

            var doc = XDocument.Load(covPath);
            var files = string.Join(", ", doc.Descendants("class").Select(c => c.Attribute("filename")!.Value));
            var pair = doc.Descendants("class").Where(c =>
                c.Attribute("filename")!.Value.Replace('\\', '/').EndsWith("/MultiPair.Codeunit.al", StringComparison.Ordinal)).ToList();
            var cls = Assert.Single(pair);
            Assert.True(cls.Attribute("filename")!.Value.Replace('\\', '/').StartsWith("bundle/", StringComparison.Ordinal),
                $"{run}: the compiled file is bundle/MultiPair.Codeunit.al, reported as {cls.Attribute("filename")!.Value}. Files: {files}\n{output}");

            var fileLines = File.ReadAllLines(Path.Combine(bundle, "MultiPair.Codeunit.al"));
            var hitTexts = cls.Descendants("line").Where(l => (int)l.Attribute("hits")! > 0)
                .Select(l => (int)l.Attribute("number")!)
                .Select(n => n <= fileLines.Length ? fileLines[n - 1].Trim() : "<past end of file>")
                .OrderBy(t => t, StringComparer.Ordinal).ToArray();
            Assert.True(executed.OrderBy(t => t, StringComparer.Ordinal).SequenceEqual(hitTexts),
                $"{run}: the lines carrying a hit read [{string.Join(" | ", hitTexts)}], expected the statements of Multi B.Reached "
                + $"[{string.Join(" | ", executed)}].\n{output}");
        }
    }
}
