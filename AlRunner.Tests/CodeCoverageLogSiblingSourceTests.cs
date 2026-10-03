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
    private readonly string _root = TestScratch.Dir("al-runner-codecoverage-log-sibling-source");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>The #5222 layout: bundle/ (compiled and run), src/ (same app id, other text), tests/.</summary>
    private void Layout(bool withSibling, bool fromSource = false)
        => CodeCoverageSiblingLayout.Write(_root, withSibling, fromSource: fromSource);

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

    private (string Output, int Exit) RunLayout(string cache, params string[] folders)
    {
        var args = new List<string> { "--isolation", "test", "--cache", cache };
        args.AddRange(TestBuildConfig.BcVersionArg.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        args.AddRange(folders.Length > 0 ? folders : new[] { "bundle", "tests" });
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

    // #5259: the same layout invoked as `tests` alone. bundle/ is no CLI bundle there: the
    // source-dependency pre-pass compiles one of the two same-app-id folders as the dependency that
    // satisfies tests/, and marks neither as compiled. The rows must be those of the folder whose
    // code ran, which the test app tells by the result (21 from bundle/, 120 from src/) and accepts
    // either, because which folder the dependency resolves to is not this fact's claim.
    [SkippableFact]
    public void Cli_CodeCoverageRows_AreTheRunText_WhenTheCompiledFolderIsASourceDependency_ColdAndWarm()
    {
        TestArtifacts.SkipIfMissing();
        Layout(withSibling: true, fromSource: true);
        var cache = Path.Combine(_root, "cache");
        foreach (var run in new[] { "cold", "warm" })
        {
            var (output, exit) = RunLayout(cache, "tests");
            Assert.True(exit == 0, $"{run}: the one test must pass, exit was {exit}.\n{output}");
            Assert.DoesNotContain("Index was outside the bounds of the array", output);
            // The run really took the dependency route, not the CLI-bundle one.
            Assert.Contains("[source-dep]", output);
        }
    }
}
