// TestExactFlagTests — `--test-exact NAME` (#5439): select a test by its WHOLE qualified name
// CodeunitNNNN.Method, where `--test PATTERN` is a substring and cannot tell GrowPre from GrowPreTwin.
//
// The fixture holds the issue's shape: one codeunit with a test and a sibling whose name it prefixes
// (GrowPre / GrowPreTwin), and a second codeunit declaring a method of the same name as the first
// (Same), so the method-name branch of `--test` hits both codeunits.
//
// Every case asserts the selected test RAN and the neighbour did NOT; `Ran` refuses a prefix match,
// because "PASS  Codeunit62152.GrowPre" is a prefix of the sibling's line.
//
// Runner-spawning like TestFilterFlagTests: --test-exact is a CLI flag with no runTests field
// (the server is refused), so the shared suite server cannot carry these.
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

public sealed class TestExactFlagTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private const string GrowPre = "Codeunit62152.GrowPre";
    private const string GrowPreTwin = "Codeunit62152.GrowPreTwin";
    private const string SameA = "Codeunit62152.Same";
    private const string SameB = "Codeunit62153.Same";

    private readonly string _root;
    private string? _jobsRoot;

    public TestExactFlagTests()
    {
        _root = TestScratch.Dir("al-runner-test-exact-flag");
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "app.json"), """
        {
          "id": "b2c3d4e5-f6a7-8901-2345-67890abcdf52",
          "name": "Test Exact Flag Test Fixture",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "idRanges": [ { "from": 62150, "to": 62159 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(_root, "Grow.Codeunit.al"), """
        codeunit 62152 "TE Grow"
        {
            Subtype = Test;

            [Test]
            procedure GrowPre()
            begin
                if 1 + 1 <> 2 then
                    Error('sanity');
            end;

            [Test]
            procedure GrowPreTwin()
            begin
                if 2 + 2 <> 4 then
                    Error('sanity');
            end;

            [Test]
            procedure Same()
            begin
                if 3 + 3 <> 6 then
                    Error('sanity');
            end;
        }
        """);
        File.WriteAllText(Path.Combine(_root, "Other.Codeunit.al"), """
        codeunit 62153 "TE Other"
        {
            Subtype = Test;

            [Test]
            procedure Same()
            begin
                if 4 + 4 <> 8 then
                    Error('sanity');
            end;
        }
        """);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        if (_jobsRoot != null) try { Directory.Delete(_jobsRoot, recursive: true); } catch { }
    }

    private (string output, int exit) RunRunner(params string[] extraArgs)
        => RunRunnerOn(new[] { _root }, extraArgs);

    private (string output, int exit) RunRunnerOn(string[] bundles, params string[] extraArgs)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append(" --strict");
        foreach (var b in bundles) args.Append($" \"{b}\"");
        foreach (var a in extraArgs) args.Append($" {a}");
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        var sb = new StringBuilder();
        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived  += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(240_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    /// <summary>True when <paramref name="name"/> ran and passed, as a whole name: GrowPre is not GrowPreTwin.</summary>
    private static bool Ran(string output, string name)
        => Regex.IsMatch(output, $@"PASS +{Regex.Escape(name)}(?![A-Za-z0-9_])");

    /// <summary>Control, and the pin that substring semantics did not move: `--test` still selects the sibling too.</summary>
    [SkippableFact]
    public void TestFlag_Substring_StillSelectsTheSiblingToo()
    {
        TestArtifacts.SkipIfMissing();

        var (output, exit) = RunRunner("--test Codeunit62152.GrowPre");

        Assert.True(exit == 0, output);
        Assert.True(Ran(output, GrowPre), output);
        Assert.True(Ran(output, GrowPreTwin), output);
        Assert.False(Ran(output, SameA), output);
        Assert.Contains("Tests: 2 ", output);
    }

    /// <summary>The issue's case: the exact name runs GrowPre alone, not GrowPreTwin.</summary>
    [SkippableFact]
    public void TestExact_SelectsOneOfTwoSiblings()
    {
        TestArtifacts.SkipIfMissing();

        var (output, exit) = RunRunner("--test-exact Codeunit62152.GrowPre");

        Assert.True(exit == 0, output);
        Assert.True(Ran(output, GrowPre), output);
        Assert.False(Ran(output, GrowPreTwin), output);
        Assert.False(Ran(output, SameA), output);
        Assert.False(Ran(output, SameB), output);
        Assert.Contains("Tests: 1 ", output);
    }

    /// <summary>
    /// The name is the codeunit AND the method: `--test Same` runs both codeunits' Same (the method-name
    /// branch), the exact name runs the named codeunit's only. Names the other one next, so a selector that
    /// ignored the codeunit half would run the wrong test and still show one PASS.
    /// </summary>
    [SkippableFact]
    public void TestExact_SameMethodNameInTwoCodeunits_SelectsTheNamedCodeunitOnly()
    {
        TestArtifacts.SkipIfMissing();

        var (substring, substringExit) = RunRunner("--test Same");
        Assert.True(substringExit == 0, substring);
        Assert.True(Ran(substring, SameA) && Ran(substring, SameB), substring);

        var (output, exit) = RunRunner("--test-exact Codeunit62153.Same");
        Assert.True(exit == 0, output);
        Assert.True(Ran(output, SameB), output);
        Assert.False(Ran(output, SameA), output);
        Assert.Contains("Tests: 1 ", output);
    }

    /// <summary>Case-insensitive like --test, and repeatable: two names run exactly those two.</summary>
    [SkippableFact]
    public void TestExact_IsCaseInsensitive_AndRepeatable()
    {
        TestArtifacts.SkipIfMissing();

        var (output, exit) = RunRunner("--test-exact codeunit62152.GROWPRETWIN", "--test-exact Codeunit62153.same");

        Assert.True(exit == 0, output);
        Assert.True(Ran(output, GrowPreTwin), output);
        Assert.True(Ran(output, SameB), output);
        Assert.False(Ran(output, GrowPre), output);
        Assert.False(Ran(output, SameA), output);
        Assert.Contains("Tests: 2 ", output);
    }

    /// <summary>
    /// A name that is not a whole qualified name selects nothing and fails with exit 6, naming the flag:
    /// an unknown test, a prefix of a real method, and a bare codeunit are all misses, never a substring hit.
    /// </summary>
    [SkippableTheory]
    [InlineData("Codeunit62152.NoSuchTest")]
    [InlineData("Codeunit62152.GrowP")]
    [InlineData("Codeunit62152")]
    public void TestExact_NotAWholeQualifiedName_IsExit6(string name)
    {
        TestArtifacts.SkipIfMissing();

        var (output, exit) = RunRunner($"--test-exact {name}");

        Assert.True(exit == 6, output);
        Assert.Contains($"--test-exact '{name}' selected no test in this run", output);
        Assert.False(Ran(output, GrowPre) || Ran(output, GrowPreTwin) || Ran(output, SameA) || Ran(output, SameB), output);
    }

    /// <summary>
    /// --exclude-test composes: an excluded name drops out of the exact selection; and when the exclusions
    /// leave nothing to run the run is exit 6 naming --exclude-test, not a silent exit 0 (the issue's
    /// edge case), for --test-exact and for --test alike. A partial exclusion stays exit 0.
    /// </summary>
    [SkippableFact]
    public void ExcludeTest_CombinedWithSelection_DropsNames_AndEmptyingTheSelectionIsExit6()
    {
        TestArtifacts.SkipIfMissing();

        var (partial, partialExit) = RunRunner(
            "--test-exact Codeunit62152.GrowPre", "--test-exact Codeunit62152.GrowPreTwin",
            "--exclude-test Codeunit62152.GrowPre");
        Assert.True(partialExit == 0, partial);
        Assert.True(Ran(partial, GrowPreTwin), partial);
        Assert.False(Ran(partial, GrowPre), partial);

        var (emptied, emptiedExit) = RunRunner(
            "--test-exact Codeunit62152.GrowPre", "--exclude-test Codeunit62152.GrowPre");
        Assert.True(emptiedExit == 6, emptied);
        Assert.Contains("--exclude-test names every one of them, so nothing is left to run", emptied);

        var (substring, substringExit) = RunRunner(
            "--test Codeunit62152.GrowPre",
            "--exclude-test Codeunit62152.GrowPre", "--exclude-test Codeunit62152.GrowPreTwin");
        Assert.True(substringExit == 6, substring);
        Assert.Contains("--exclude-test names every one of them, so nothing is left to run", substring);
    }

    /// <summary>--server drops a startup exact selection on every runTests call, so it is refused (exit 2), not ignored.</summary>
    [SkippableFact]
    public void TestExact_WithServer_IsRefused()
    {
        TestArtifacts.SkipIfMissing();

        var (output, exit) = RunRunner("--server", "--test-exact Codeunit62152.GrowPre");

        Assert.True(exit == 2, output);
        Assert.Contains("--test-exact is not supported with --server", output);
    }

    /// <summary>
    /// `--test` and `--test-exact` together are the INTERSECTION. Exit-6 half: the substring selects only
    /// GrowPreTwin and the exact name only GrowPre, so nothing is in both (a run that dropped `--test` would
    /// run GrowPre). Positive half: a substring that does contain the exact name leaves that one test, not its sibling.
    /// </summary>
    [SkippableFact]
    public void TestAndTestExact_Together_AreTheIntersection()
    {
        TestArtifacts.SkipIfMissing();

        var (disjoint, disjointExit) = RunRunner("--test GrowPreTwin", "--test-exact Codeunit62152.GrowPre");
        Assert.True(disjointExit == 6, disjoint);
        Assert.Contains("--test 'GrowPreTwin' with --test-exact 'Codeunit62152.GrowPre' selected no test in this run", disjoint);
        Assert.False(Ran(disjoint, GrowPre) || Ran(disjoint, GrowPreTwin), disjoint);

        var (both, bothExit) = RunRunner("--test GrowPre", "--test-exact Codeunit62152.GrowPre");
        Assert.True(bothExit == 0, both);
        Assert.True(Ran(both, GrowPre), both);
        Assert.False(Ran(both, GrowPreTwin), both);
        Assert.Contains("Tests: 1 ", both);
    }

    private (string a, string b) WriteJobsBundles()
    {
        // Outside _root: _root is itself a bundle, and nesting these would add them to it.
        _jobsRoot = TestScratch.Dir("al-runner-test-exact-jobs");
        var a = Path.Combine(_jobsRoot, "a");
        var b = Path.Combine(_jobsRoot, "b");
        foreach (var (dir, id, guid, name, method) in new[]
                 {
                     (a, 62156, "b2c3d4e5-f6a7-8901-2345-67890abcdf56", "TE Jobs A", "OnlyA"),
                     (b, 62157, "b2c3d4e5-f6a7-8901-2345-67890abcdf57", "TE Jobs B", "OnlyB"),
                 })
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
            {
              "id": "{{guid}}",
              "name": "{{name}}",
              "publisher": "AL Runner",
              "version": "1.0.0.0",
              "dependencies": [],
              "idRanges": [ { "from": 62150, "to": 62159 } ],
              "runtime": "14.0"
            }
            """);
            File.WriteAllText(Path.Combine(dir, "Test.Codeunit.al"), $$"""
            codeunit {{id}} "{{name}}"
            {
                Subtype = Test;

                [Test]
                procedure {{method}}()
                begin
                    if 1 + 1 <> 2 then
                        Error('sanity');
                end;
            }
            """);
        }
        return (a, b);
    }

    /// <summary>
    /// `--jobs`: the flag reaches the workers and the PARENT judges the zero for the whole invocation. An
    /// exact name living in one shard only is exit 0 with exactly that test run (a worker judging its own
    /// shard would exit 6 for the other); a name in no shard is exit 6 naming `--test-exact`.
    /// </summary>
    [SkippableFact]
    public void Jobs_TestExact_InOneShardOnly_Passes_AndInNoShard_IsExit6()
    {
        TestArtifacts.SkipIfMissing();
        var (a, b) = WriteJobsBundles();

        var (ok, okExit) = RunRunnerOn(new[] { a, b }, "--jobs 2", "--test-exact Codeunit62157.OnlyB");
        Assert.True(okExit == 0, ok);
        Assert.Contains("jobs: 2 bundle(s) across 2 worker process(es)", ok);
        Assert.True(Ran(ok, "Codeunit62157.OnlyB"), ok);
        Assert.False(Ran(ok, "Codeunit62156.OnlyA"), ok);
        Assert.Contains("Tests: 1 ", ok);

        var (none, noneExit) = RunRunnerOn(new[] { a, b }, "--jobs 2", "--test-exact Codeunit62157.NoSuch");
        Assert.True(noneExit == 6, none);
        Assert.Contains("--test-exact 'Codeunit62157.NoSuch' selected no test in this run", none);
    }

    /// <summary>`--watch --affected` selects its own tests, so an explicit selection is refused (exit 2) before any watch loop starts.</summary>
    [SkippableFact]
    public void TestExact_WithWatchAffected_IsRefused()
    {
        TestArtifacts.SkipIfMissing();

        var (output, exit) = RunRunner("--watch", "--affected", "--test-exact Codeunit62152.GrowPre");

        Assert.True(exit == 2, output);
        Assert.Contains("--affected cannot be combined with --test/--filter/--test-exact", output);
    }
}
