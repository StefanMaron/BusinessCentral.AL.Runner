// SuiteEnumerationTests — guards EnumerateSuites against the multi-app parent-dir
// collapse reported in #1623 / #1638.
//
// The bug: LooksLikeSuite only recognised a directory as a suite if it contained a
// `test/` or `src/` subfolder. Every tests/runner-extras suite is flat (app.json +
// .al files, no test//src/), so none matched; EnumerateSuites fell through to its
// "flat bundle" fallback and yielded the ENTIRE parent tree as a single compile
// unit. Only one suite's tests survived that collapse — the rest were silently
// dropped with no compile error and no warning. Both CI legs invoke
// `al-runner tests/runner-extras --strict`, so ~97% of that gate was dead.
//
// Test A (RED before the fix): a parent dir holding three flat app.json suites must
// run all three, not one.
// Test B (regression): a directory that IS itself one app — the shape of the
// al-language corpus, which has app.json at its root and no test//src/ anywhere —
// must still collapse to exactly one bucket. This is the trap in the obvious fix:
// making app.json a suite marker without checking the root first would leave the
// corpus working, but making it a *child-only* marker would break it.
//
// Deleting the app.json clause from LooksLikeSuite makes A fail; making
// EnumerateSuites always descend makes B fail.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

// See DefineFlagIntegrationTests for why this used to be serialized with the
// other runner-subprocess integration tests and no longer is — #1809.
public sealed class SuiteEnumerationTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private readonly string _root;

    public SuiteEnumerationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "al-runner-suite-enum", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>
    /// Writes a flat AL suite (app.json + one test codeunit, no test//src/ subdirs)
    /// into <paramref name="dir"/>. Each suite gets its own app id and object id
    /// range so the three can compile independently.
    /// </summary>
    private static void WriteFlatSuite(string dir, string appId, int baseId, string tag)
    {
        Directory.CreateDirectory(dir);

        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "{{appId}}",
          "name": "Suite Enum Fixture {{tag}}",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "application": "1.0.0.0",
          "idRanges": [ { "from": {{baseId}}, "to": {{baseId + 9}} } ],
          "runtime": "14.0"
        }
        """);

        // The assertion carries the suite's own baseId, so a collapsed run cannot
        // satisfy all three from one compile unit — and a suite that silently fails
        // to emit shows up as a missing PASS line rather than a vacuous green.
        File.WriteAllText(Path.Combine(dir, $"SuiteEnum{tag}.Codeunit.al"), $$"""
        codeunit {{baseId}} "Suite Enum {{tag}}"
        {
            Subtype = Test;

            [Test]
            procedure SuiteRan{{tag}}()
            var
                Actual: Integer;
            begin
                Actual := {{baseId}};
                if Actual <> {{baseId}} then
                    Error('Suite {{tag}} did not run its own compile unit');
            end;
        }
        """);
    }

    private (string output, int exit) RunRunner(string target)
    {
        var args = new StringBuilder(
            TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append($" \"{target}\"");
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

    private static string CurrentFramework()
    {
        var v = Environment.Version;
        return $"net{v.Major}.{v.Minor}";
    }

    /// <summary>
    /// Reads the per-bundle "— N suites" line. This is the number under test: a
    /// directory of suites is still ONE bundle (one "bucket"), but must enumerate
    /// every suite inside it. Asserting on the bucket count would prove nothing.
    /// </summary>
    private static int SuiteCount(string output)
    {
        var m = System.Text.RegularExpressions.Regex.Match(output, @"—\s*(\d+)\s*suites");
        Assert.True(m.Success, $"run output had no suite count line. Output:\n{output}");
        return int.Parse(m.Groups[1].Value);
    }

    /// <summary>Reads "Tests:  N total" out of the run summary.</summary>
    private static int TestCount(string output)
    {
        var m = System.Text.RegularExpressions.Regex.Match(output, @"Tests:\s*(\d+)\s*total");
        Assert.True(m.Success, $"run summary had no test count. Output:\n{output}");
        return int.Parse(m.Groups[1].Value);
    }

    /// <summary>
    /// RED before the fix: three flat app.json suites under one parent collapsed to a
    /// single bucket and only one suite's tests ran. All three must run.
    /// </summary>
    [SkippableFact]
    public void ParentDirWithFlatAppJsonChildren_RunsEverySuite()
    {
        TestArtifacts.SkipIfMissing();

        WriteFlatSuite(Path.Combine(_root, "alpha"), "aaaaaaaa-1111-4111-8111-aaaaaaaaaaaa", 62200, "Alpha");
        WriteFlatSuite(Path.Combine(_root, "beta"), "bbbbbbbb-2222-4222-8222-bbbbbbbbbbbb", 62210, "Beta");
        WriteFlatSuite(Path.Combine(_root, "gamma"), "cccccccc-3333-4333-8333-cccccccccccc", 62220, "Gamma");

        var (output, _) = RunRunner(_root);

        // Before the fix: 1 suite, 1 test — two of the three were silently dropped.
        Assert.Equal(3, SuiteCount(output));
        Assert.Equal(3, TestCount(output));
        Assert.Contains("SuiteRanAlpha", output);
        Assert.Contains("SuiteRanBeta", output);
        Assert.Contains("SuiteRanGamma", output);
    }

    /// <summary>
    /// Regression guard for the al-language corpus shape: app.json at the root, no
    /// test//src/ subdirectories, .al files spread across category folders. That is
    /// ONE app and must stay one bucket — the fix must check the root before
    /// descending into children.
    /// </summary>
    [SkippableFact]
    public void SingleAppWithCategorySubdirs_StaysOneBucket()
    {
        TestArtifacts.SkipIfMissing();

        WriteFlatSuite(_root, "dddddddd-4444-4444-8444-dddddddddddd", 62230, "Solo");
        // Category subdirectory with an extra .al file and NO app.json of its own —
        // this must not be mistaken for a separate suite.
        var category = Path.Combine(_root, "collections");
        Directory.CreateDirectory(category);
        File.WriteAllText(Path.Combine(category, "Helper.Codeunit.al"), """
        codeunit 62231 "Suite Enum Solo Helper"
        {
            procedure Value(): Integer
            begin
                exit(7);
            end;
        }
        """);

        var (output, _) = RunRunner(_root);

        // The category sub-directory must not be mistaken for a second suite.
        Assert.Equal(1, SuiteCount(output));
        Assert.Equal(1, TestCount(output));
        Assert.Contains("SuiteRanSolo", output);
    }
}
