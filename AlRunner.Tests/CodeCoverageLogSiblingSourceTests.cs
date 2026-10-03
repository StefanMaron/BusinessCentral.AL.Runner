// CodeCoverageLogSiblingSourceTests — #5250: the Code Coverage (2000000049) line rows read an
// object's text from CodeCoveragePatches.CompiledSourceMap, which built its map over every
// registered source dir as one plain list. A sibling source folder with the SAME app id beside
// the compiled folder (the #5222 layout) is registered after the compiled one, so Build's
// last-root-wins replaced the compiled text with the sibling's. The statement lines the recorder
// decodes are numbered in the COMPILED text, so reading the sibling's shorter text indexed past
// its end and the FindSet on Record "Code Coverage" threw "Index was outside the bounds of the
// array." (measured on the #5222 layout before the fix).
//
// The execution roots are the dirs the suite-registration loops register (Program.cs), marked by
// RecordPatches.AddExecutionSourceDirs; AlCoverageSourceMap.Build then never lets a non-execution
// root replace an object an execution root declares. docs/coverage-attribution.md
// #a-sibling-source-folder-beside-an-execution-root-5222.
//
// Layers: the issue's layout through the CLI (cold and warm against one cache root), the served
// text in-process, and a source-reading guard that no production caller builds the map from a
// plain list of registered dirs again.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

public sealed class CodeCoverageLogSiblingSourceTests : IDisposable
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

    // What is compiled: Multi A is longer, so every line of Multi B sits further down than it
    // does in SourceText, and the file is longer than the sibling's.
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

    // Reads the Code Coverage rows of Multi B after one call to Reached(20), and fails with the
    // rows it saw unless they are exactly the compiled text's code lines and hits.
    private const string TestsText = """
        codeunit 79811 "Multi Tests"
        {
            Subtype = Test;

            [Test]
            procedure CodeCoverageRowsFollowTheCompiledText()
            var
                B: Codeunit "Multi B";
                CC: Record "Code Coverage";
                Seen: Text;
            begin
                CodeCoverageLog(true, false);
                B.Reached(20);
                CodeCoverageLog(false, false);
                CC.SetRange("Object Type", CC."Object Type"::Codeunit);
                CC.SetRange("Object ID", 79801);
                CC.SetRange("Line Type", CC."Line Type"::Code);
                if CC.FindSet() then
                    repeat
                        Seen += StrSubstNo('[%1:%2]', DelChr(CC.Line, '<', ' '), CC."No. of Hits");
                    until CC.Next() = 0;
                if Seen <> '[Z := 1;:1][if X > 10 then:1][exit(X + 1);:1][exit(X);:0][exit(X * 3);:0]' then
                    Error('Code Coverage rows were %1', Seen);
            end;
        }
        """;

    private readonly string _root = TestScratch.Dir("al-runner-codecoverage-log-sibling-source");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static void WriteManifest(string dir, Guid id, string name, bool dependsOnApp, bool platform = false)
    {
        Directory.CreateDirectory(dir);
        var manifest = new Dictionary<string, object>
        {
            ["id"] = id, ["name"] = name, ["publisher"] = "AL Runner", ["version"] = "1.0.0.0",
            ["runtime"] = "14.0",
            ["idRanges"] = new[] { new { from = 79800, to = 79830 } },
            ["dependencies"] = dependsOnApp
                ? new[] { new { id = AppId, name = "Multi", publisher = "AL Runner", version = "1.0.0.0" } }
                : Array.Empty<object>(),
        };
        // Record "Code Coverage" is a system table, so the test app needs the platform symbols.
        if (platform) manifest["platform"] = "27.0.0.0";
        File.WriteAllText(Path.Combine(dir, "app.json"), JsonSerializer.Serialize(manifest));
    }

    /// <summary>The #5222 layout: bundle/ (compiled and run), src/ (same app id, other text), tests/.</summary>
    private void Layout(bool withSibling)
    {
        WriteManifest(Path.Combine(_root, "bundle"), AppId, "Multi", dependsOnApp: false);
        Write(Path.Combine(_root, "bundle", "MultiPair.Codeunit.al"), BundleText);
        if (withSibling)
        {
            WriteManifest(Path.Combine(_root, "src"), AppId, "Multi", dependsOnApp: false);
            Write(Path.Combine(_root, "src", "MultiPair.Codeunit.al"), SourceText);
        }
        WriteManifest(Path.Combine(_root, "tests"), TestAppId, "Multi Tests", dependsOnApp: true, platform: true);
        Write(Path.Combine(_root, "tests", "src", "MultiTests.Codeunit.al"), TestsText);
    }

    // The shape that let the defect be fixed at one site and left at another: a production caller
    // handing Build a plain list of dirs, where the last root wins. Every caller must go through
    // a helper that marks the execution roots. Reads the production source, so a new caller
    // fails here by name.
    //
    // Allowlisted: the DAP syntax index builds over the launched bundle's own compiled folders
    // only (Program.cs, `syntaxSourceMap`), so there is no sibling folder in that list to prefer.
    [Fact]
    public void EveryProductionBuildCaller_MarksTheExecutionRoots()
    {
        var plain = new List<string>();
        var viaParsed = 0;
        var viaRegistered = 0;
        var allowlisted = 0;
        foreach (var file in Directory.EnumerateFiles(ProjectPath, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(ProjectPath, file).Replace('\\', '/');
            if (rel.StartsWith("bin/") || rel.StartsWith("obj/")) continue;
            var text = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(text, @"AlCoverageSourceMap\.Build\(", RegexOptions.None))
            {
                var end = text.IndexOf(';', m.Index);
                var statement = text.Substring(m.Index, end - m.Index);
                var lineStart = text.LastIndexOf('\n', m.Index) + 1;
                var head = text.Substring(lineStart, m.Index - lineStart);
                if (head.TrimStart().StartsWith("//")) continue;   // a comment naming the call
                if (statement.Contains("RootsWithParsedSourceDependencies"))
                    viaParsed++;
                else if (statement.Contains("RootsForRegisteredDirs"))
                    viaRegistered++;
                else if (rel == "Program.cs" && head.Contains("syntaxSourceMap"))
                    allowlisted++;
                else
                    plain.Add($"{rel}: {head.Trim()}{statement.Split('\n')[0].Trim()}");
            }
        }
        Assert.True(plain.Count == 0,
            "AlCoverageSourceMap.Build is called over a plain list of roots, so a same-app-id sibling "
            + "source folder registered after the compiled one replaces its text (#5222, #5250). Build "
            + "the roots with RootsWithParsedSourceDependencies or RootsForRegisteredDirs:\n" + string.Join("\n", plain));
        // The scan found what it should: an allowlist entry that matches nothing is stale, and no
        // caller of a helper would mean the pattern above stopped seeing them.
        Assert.Equal(1, allowlisted);
        Assert.True(viaParsed > 0, "found no Build caller using RootsWithParsedSourceDependencies");
        Assert.True(viaRegistered > 0, "found no Build caller using RootsForRegisteredDirs");
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

    private (string Output, int Exit) RunLayout(string cache)
    {
        var args = new List<string> { "--isolation", "test", "--cache", cache };
        args.AddRange(TestBuildConfig.BcVersionArg.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        args.AddRange(new[] { "bundle", "tests" });
        return SpawnRunner(args.ToArray());
    }

    // The control: without the sibling the rows come back. Both runs read one cache root, so the
    // warm run is read as well as the cold one.
    [SkippableFact]
    public void Cli_CodeCoverageRows_AreTheCompiledText_WithoutASibling_ColdAndWarm()
    {
        TestArtifacts.SkipIfMissing();
        Layout(withSibling: false);
        var cache = Path.Combine(_root, "cache");
        foreach (var run in new[] { "cold", "warm" })
        {
            var (output, exit) = RunLayout(cache);
            Assert.True(exit == 0, $"{run}: the one test must pass, exit was {exit}.\n{output}");
        }
    }

    // #5250: the same layout with a same-app-id sibling folder registered beside bundle/.
    [SkippableFact]
    public void Cli_CodeCoverageRows_AreTheCompiledText_BesideASameAppIdSiblingFolder_ColdAndWarm()
    {
        TestArtifacts.SkipIfMissing();
        Layout(withSibling: true);
        var cache = Path.Combine(_root, "cache");
        foreach (var run in new[] { "cold", "warm" })
        {
            var (output, exit) = RunLayout(cache);
            Assert.True(exit == 0, $"{run}: the one test must pass, exit was {exit}.\n{output}");
            Assert.DoesNotContain("Index was outside the bounds of the array", output);
        }
    }
}
