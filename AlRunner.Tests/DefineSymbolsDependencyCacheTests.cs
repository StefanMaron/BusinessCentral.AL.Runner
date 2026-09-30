// DefineSymbolsDependencyCacheTests — issue #4990.
//
// Two runs over one unchanged dependency + test pair, on ONE --cache root, differing only in
// --define, must each compile and run against the dependency as THEIR symbols declare it. Two
// persisted keys sit downstream of --define and carried no term for it:
//
//   workspace-deps/<key>  ProgramSupport.ComputeSourceWorkspaceKey — the dependency's
//                         *.symbols.json the test app COMPILES against (the declaration shape);
//   compiled-deps/<key>   DependencyLoader.ComputeSourceDependencyCacheKeyCore — the DLL the
//                         test RUNS against when the dependency is reached only as a sibling
//                         (the body shape).
//
// Each test runs three times — none, FLAG, none — and also counts the entries on disk: two
// distinct symbol sets must give exactly two entries, so a key that never moves (the defect)
// and a key that never repeats (a destroyed cache) both fail.

using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class DefineSymbolsDependencyCacheTests : IDisposable
{
    private const int SpawnTimeoutMs = 240_000;

    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private readonly string _root;
    private readonly string _cache;

    public DefineSymbolsDependencyCacheTests()
    {
        _root = TestScratch.Dir("al-runner-define-dep-cache");
        _cache = Path.Combine(_root, "cache");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private const string AppJson = """
        {"id":"7d1c3a52-0b6e-4f3e-9a41-5e2b8c9d4990","name":"define-cache-app","publisher":"repro","version":"1.0.0.0","runtime":"16.0","idRanges":[{"from":50100,"to":50149}]}
        """;

    private const string TestJson = """
        {"id":"7d1c3a52-0b6e-4f3e-9a41-5e2b8c9e4990","name":"define-cache-test","publisher":"repro","version":"1.0.0.0","runtime":"16.0","idRanges":[{"from":50150,"to":50199}],"dependencies":[{"id":"7d1c3a52-0b6e-4f3e-9a41-5e2b8c9d4990","name":"define-cache-app","publisher":"repro","version":"1.0.0.0"}]}
        """;

    private void WriteFixture(string answer, string test)
    {
        var app = Directory.CreateDirectory(Path.Combine(_root, "app")).FullName;
        var tst = Directory.CreateDirectory(Path.Combine(_root, "test")).FullName;
        File.WriteAllText(Path.Combine(app, "app.json"), AppJson);
        File.WriteAllText(Path.Combine(app, "Answer.Codeunit.al"), answer);
        File.WriteAllText(Path.Combine(tst, "app.json"), TestJson);
        File.WriteAllText(Path.Combine(tst, "AnswerTests.Codeunit.al"), test);
    }

    private (string Output, int Exit) Run(bool flag, params string[] bundles)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(Path.Combine(RepoRoot, "AlRunner")));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append($" --cache \"{_cache}\" --isolation test");
        if (flag) args.Append(" --define FLAG");
        foreach (var b in bundles) args.Append($" \"{Path.Combine(_root, b)}\"");

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        var sb = new StringBuilder();
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(SpawnTimeoutMs)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    private static void AssertPassed((string Output, int Exit) r, string run)
    {
        Assert.True(r.Exit == 0, $"{run}: exit {r.Exit}\n{r.Output}");
        Assert.Contains("passed 1", r.Output);
        Assert.Empty(RunnerFailureLines.All(r.Output));
    }

    private int Entries(string cacheName, string pattern, bool directories) =>
        directories
            ? Directory.GetDirectories(Path.Combine(_cache, cacheName)).Length
            : Directory.GetFiles(Path.Combine(_cache, cacheName), pattern).Length;

    /// <summary>
    /// The reporter's shape: the symbols select which procedure the dependency DECLARES, so
    /// a stale *.symbols.json fails the test compile with AL0132.
    /// </summary>
    [SkippableFact]
    public void DeclarationGatedByDefine_OneCacheRoot_CompilesAgainstEachRunsDependency()
    {
        TestArtifacts.SkipIfMissing();
        WriteFixture(
            """
            codeunit 50100 Answer
            {
            #if FLAG
                procedure ValueA(): Integer
            #else
                procedure ValueB(): Integer
            #endif
                begin
                    exit(1);
                end;
            }
            """,
            """
            codeunit 50150 AnswerTests
            {
                Subtype = Test;

                [Test]
                procedure CallsTheBuildsName()
                var
                    A: Codeunit Answer;
                begin
            #if FLAG
                    if A.ValueA() <> 1 then
                        Error('ValueA() should be 1');
            #else
                    if A.ValueB() <> 1 then
                        Error('ValueB() should be 1');
            #endif
                end;
            }
            """);

        AssertPassed(Run(flag: false, "app", "test"), "run 1 (no symbols)");
        var second = Run(flag: true, "app", "test");
        Assert.DoesNotContain("AL0132", second.Output);
        AssertPassed(second, "run 2 (--define FLAG)");
        AssertPassed(Run(flag: false, "app", "test"), "run 3 (no symbols again)");

        Assert.Equal(2, Entries("workspace-deps", "*", directories: true));
    }

    /// <summary>
    /// The symbols select only a procedure BODY, and the dependency is reached only as a
    /// sibling of the one bundle passed — so what runs is the compiled-deps DLL, and a stale
    /// one returns the other build's value with nothing failing to compile.
    /// </summary>
    [SkippableFact]
    public void BodyGatedByDefine_DependencyReachedAsSibling_RunsEachRunsBuild()
    {
        TestArtifacts.SkipIfMissing();
        WriteFixture(
            """
            codeunit 50100 Answer
            {
                procedure Value(): Integer
                begin
            #if FLAG
                    exit(1);
            #else
                    exit(2);
            #endif
                end;
            }
            """,
            """
            codeunit 50150 AnswerTests
            {
                Subtype = Test;

                [Test]
                procedure ValueMatchesBuild()
                var
                    A: Codeunit Answer;
                begin
            #if FLAG
                    if A.Value() <> 1 then
                        Error('Value() should be 1 under FLAG, got %1', A.Value());
            #else
                    if A.Value() <> 2 then
                        Error('Value() should be 2 without FLAG, got %1', A.Value());
            #endif
                end;
            }
            """);

        AssertPassed(Run(flag: false, "test"), "run 1 (no symbols)");
        var second = Run(flag: true, "test");
        Assert.DoesNotContain("should be 1 under FLAG, got 2", second.Output);
        AssertPassed(second, "run 2 (--define FLAG)");
        AssertPassed(Run(flag: false, "test"), "run 3 (no symbols again)");

        Assert.Equal(2, Entries("compiled-deps", "*.dll", directories: false));
        Assert.Equal(2, Entries("workspace-deps", "*", directories: true));
    }
}
