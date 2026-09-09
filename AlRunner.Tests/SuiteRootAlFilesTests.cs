// SuiteRootAlFilesTests — #3611 / #3714: a suite's .al files outside src/, app*/ and test/
// were silently dropped from the compile.
//
// CollectSuitePaths hands the emitter a list of folders. It added the suite root ONLY when
// none of the conventional folders (src/, app*/, test/) existed — the "flat bundle" fallback.
// So one src/ folder switched the root scan off, and every .al file beside it (at the root,
// or in a sibling folder such as ControlAddin/) never reached BcCompiler.Emit:
//
//   root test codeunit + src/ helper   → "Tests: 0 total", exit 0        (#3611 case 5)
//   root helper + src/ test codeunit   → AL0185 'Probe Helper' is missing (#3611 case 4)
//   src/A.al + ControlAddin/B.al       → B.al ignored, even when broken    (#3714)
//
// The register-source-dirs loops in Program.cs made the same src/-else-root choice for
// RecordPatches, so a TABLE at the root was invisible to the in-memory table provider even
// once its file compiled. The root-table fact below is what distinguishes that half.
//
// alc compiles every one of these layouts at 0 errors; the convention was inherited from the
// legacy bucket trees and was never a contract. The fix: a suite whose .al files are not all
// under the conventional folders is compiled from its root, like a flat bundle. Suites that
// DO keep everything under src/ (and src/+test/) are unchanged — the exact-list facts below
// pin that, so the fix cannot churn every existing suite's cache key.

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

public sealed class SuiteRootAlFilesTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private readonly string _root;

    public SuiteRootAlFilesTests()
    {
        _root = TestScratch.Dir("al-runner-suite-root-al");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ── CollectSuitePaths, in-process ────────────────────────────────────────────────────

    private static void Touch(string path, string content = "")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static bool IsUnder(string file, string dir)
    {
        var rel = Path.GetRelativePath(dir, file);
        return rel != ".." && !rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !Path.IsPathRooted(rel);
    }

    /// <summary>
    /// The property the emitter needs from CollectSuitePaths: every .al file under the suite
    /// lies under at least one returned folder. Names the files that do not, so a failure
    /// says which layout dropped what.
    /// </summary>
    private static void AssertEveryAlFileCovered(string suite, IReadOnlyList<string> paths)
    {
        var all = Directory.EnumerateFiles(suite, "*.al", SearchOption.AllDirectories).ToList();
        Assert.NotEmpty(all);
        var dropped = all.Where(f => !paths.Any(p => IsUnder(f, p))).ToList();
        Assert.True(dropped.Count == 0,
            $"CollectSuitePaths returned [{string.Join(", ", paths)}], which drops: "
            + string.Join(", ", dropped.Select(d => Path.GetRelativePath(suite, d))));
        Assert.All(paths, p => Assert.True(IsUnder(p, suite) || p == suite,
            $"path {p} is outside the suite {suite}"));
    }

    [Fact]
    public void RootAlFileBesideSrc_IsCovered()
    {
        Touch(Path.Combine(_root, "app.json"), "{}");
        Touch(Path.Combine(_root, "Helper.Codeunit.al"));
        Touch(Path.Combine(_root, "src", "Tests.Codeunit.al"));

        AssertEveryAlFileCovered(_root, ProgramSupport.CollectSuitePaths(_root));
    }

    [Fact]
    public void SiblingDirBesideSrc_IsCovered()
    {
        Touch(Path.Combine(_root, "app.json"), "{}");
        Touch(Path.Combine(_root, "src", "A.al"));
        Touch(Path.Combine(_root, "ControlAddin", "B.al"));

        AssertEveryAlFileCovered(_root, ProgramSupport.CollectSuitePaths(_root));
    }

    [Fact]
    public void RootAlFileBesideAppDir_IsCovered()
    {
        Touch(Path.Combine(_root, "app.json"), "{}");
        Touch(Path.Combine(_root, "app", "A.al"));
        Touch(Path.Combine(_root, "B.al"));

        AssertEveryAlFileCovered(_root, ProgramSupport.CollectSuitePaths(_root));
    }

    [Fact]
    public void RootAlFileBesideTestDir_IsCovered()
    {
        Touch(Path.Combine(_root, "app.json"), "{}");
        Touch(Path.Combine(_root, "test", "A.al"));
        Touch(Path.Combine(_root, "B.al"));

        AssertEveryAlFileCovered(_root, ProgramSupport.CollectSuitePaths(_root));
    }

    /// <summary>
    /// Regression pin: an app that keeps everything under src/ still gets exactly [src].
    /// ComputeAlCacheKey hashes the folder-relative layout, so widening this list would miss
    /// every existing suite's cache once for no gain.
    /// </summary>
    [Fact]
    public void SrcOnly_StillReturnsExactlySrc()
    {
        Touch(Path.Combine(_root, "app.json"), "{}");
        Touch(Path.Combine(_root, "src", "A.al"));
        Touch(Path.Combine(_root, "src", "deep", "B.al"));

        var paths = ProgramSupport.CollectSuitePaths(_root);

        Assert.Equal(new[] { Path.Combine(_root, "src") }, paths);
    }

    [Fact]
    public void SrcAndTest_StillReturnsExactlySrcThenTest()
    {
        Touch(Path.Combine(_root, "app.json"), "{}");
        Touch(Path.Combine(_root, "src", "A.al"));
        Touch(Path.Combine(_root, "test", "B.al"));

        var paths = ProgramSupport.CollectSuitePaths(_root);

        Assert.Equal(new[] { Path.Combine(_root, "src"), Path.Combine(_root, "test") }, paths);
    }

    [Fact]
    public void Flat_StillReturnsExactlyTheSuiteRoot()
    {
        Touch(Path.Combine(_root, "app.json"), "{}");
        Touch(Path.Combine(_root, "A.al"));
        Touch(Path.Combine(_root, "collections", "B.al"));

        var paths = ProgramSupport.CollectSuitePaths(_root);

        Assert.Equal(new[] { _root }, paths);
    }

    /// <summary>
    /// A file that is not AL beside src/ must NOT flip the suite to root-scanning: only .al
    /// files count. Otherwise every suite with a README at its root would churn its cache key.
    /// </summary>
    [Fact]
    public void NonAlFileBesideSrc_DoesNotWidenThePaths()
    {
        Touch(Path.Combine(_root, "app.json"), "{}");
        Touch(Path.Combine(_root, "README.md"), "# not AL");
        Touch(Path.Combine(_root, "src", "A.al"));

        var paths = ProgramSupport.CollectSuitePaths(_root);

        Assert.Equal(new[] { Path.Combine(_root, "src") }, paths);
    }

    // ── end to end, spawning the runner ──────────────────────────────────────────────────

    private static void WriteManifest(string dir, string appId, int idFrom, string name)
    {
        Directory.CreateDirectory(dir);
        // No "application" property — see .claude/rules/no-base-app-in-csharp-tests.md.
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "{{appId}}",
          "name": "{{name}}",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": {{idFrom}}, "to": {{idFrom + 19}} } ],
          "runtime": "14.0"
        }
        """);
    }

    private static string HelperCodeunit(int id, string tag) => $$"""
        codeunit {{id}} "SRAF Helper {{tag}}"
        {
            procedure Answer(): Integer
            begin
                exit(42);
            end;
        }
        """;

    private static string TestCodeunit(int id, string tag) => $$"""
        codeunit {{id}} "SRAF Tests {{tag}}"
        {
            Subtype = Test;

            [Test]
            procedure HelperAnswers{{tag}}()
            var
                H: Codeunit "SRAF Helper {{tag}}";
            begin
                // 42 is not a default: a helper that never compiled cannot satisfy this.
                if H.Answer() <> 42 then
                    Error('SRAF Helper %1 answered %2, expected 42', '{{tag}}', H.Answer());
            end;
        }
        """;

    private (string output, int exit) RunRunner(string target)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
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

    /// <summary>Reads "Tests:  N total" out of the run summary.</summary>
    private static int TestCount(string output)
    {
        var m = Regex.Match(output, @"Tests:\s*(\d+)\s*total");
        Assert.True(m.Success, $"run summary had no test count. Output:\n{output}");
        return int.Parse(m.Groups[1].Value);
    }

    private static void AssertPassed(string output, string testName)
    {
        // The reporter prints "PASS  Codeunit63301.HelperAnswers (7ms)" — object-qualified,
        // any run of spaces, optional timing — so anchor on the method name, not the prefix.
        Assert.True(Regex.IsMatch(output, $@"PASS\s+\S*\b{Regex.Escape(testName)}\b"),
            $"no PASS line for {testName}. Output:\n{output}");
    }

    /// <summary>
    /// #3611 case 5 — the silent one. The test codeunit sits at the root, its helper under
    /// src/. Before the fix: "Tests: 0 total", exit 0, and a CI pipeline reports success
    /// having run nothing.
    /// </summary>
    [SkippableFact]
    public void TestAtRoot_HelperInSrc_RunsTheTest()
    {
        TestArtifacts.SkipIfMissing();

        WriteManifest(_root, "5a3f0b11-3611-4a01-8001-000000003611", 63300, "SRAF Inverse");
        Touch(Path.Combine(_root, "Tests.Codeunit.al"), TestCodeunit(63301, "Inverse"));
        Touch(Path.Combine(_root, "src", "Helper.Codeunit.al"), HelperCodeunit(63300, "Inverse"));

        var (output, exit) = RunRunner(_root);

        Assert.Equal(1, TestCount(output));
        AssertPassed(output, "HelperAnswersInverse");
        Assert.Equal(0, exit);
    }

    /// <summary>
    /// #3611 case 4 — the loud one. Helper at the root, the test under src/. Before the fix:
    /// AL0185 'SRAF Helper Nested' is missing, EMIT-ZERO, and a diagnosis that points at
    /// dependencies rather than at layout.
    /// </summary>
    [SkippableFact]
    public void HelperAtRoot_TestInSrc_RunsTheTest()
    {
        TestArtifacts.SkipIfMissing();

        WriteManifest(_root, "5a3f0b11-3611-4a02-8002-000000003611", 63310, "SRAF Nested");
        Touch(Path.Combine(_root, "Helper.Codeunit.al"), HelperCodeunit(63310, "Nested"));
        Touch(Path.Combine(_root, "src", "Tests.Codeunit.al"), TestCodeunit(63311, "Nested"));

        var (output, exit) = RunRunner(_root);

        Assert.DoesNotContain("AL0185", output);
        Assert.Equal(1, TestCount(output));
        AssertPassed(output, "HelperAnswersNested");
        Assert.Equal(0, exit);
    }

    /// <summary>
    /// #3714 — a sibling folder beside src/ (the Continia Document Output shape:
    /// ControlAddin/ next to src/). Its file is deliberately broken. Before the fix the
    /// folder was never read, so the bundle compiled clean and exited 0 with an object
    /// missing. After: the compile fails and the diagnostic names the ignored file.
    /// </summary>
    [SkippableFact]
    public void BrokenAlInSiblingDirBesideSrc_FailsTheCompileNamingTheFile()
    {
        TestArtifacts.SkipIfMissing();

        WriteManifest(_root, "5a3f0b11-3714-4a03-8003-000000003714", 63320, "SRAF Sibling");
        Touch(Path.Combine(_root, "src", "Helper.Codeunit.al"), HelperCodeunit(63320, "Sibling"));
        Touch(Path.Combine(_root, "src", "Tests.Codeunit.al"), TestCodeunit(63321, "Sibling"));
        Touch(Path.Combine(_root, "ControlAddin", "Broken.Codeunit.al"),
            "codeunit 63322 \"SRAF Broken Sibling\" { this is not AL }");

        var (output, exit) = RunRunner(_root);

        Assert.Contains("Broken.Codeunit.al", output);
        Assert.Equal(3, exit);
    }

    /// <summary>
    /// The register-source-dirs half. A TABLE at the root, used by a test under src/. Emitting
    /// the root file is necessary but not sufficient: RecordPatches only knows tables from the
    /// folders Program.cs registered, and those loops made the same src/-else-root choice.
    /// Insert-then-Get of a non-default value proves the table exists in the in-memory
    /// provider, not merely in the compiled module.
    /// </summary>
    [SkippableFact]
    public void TableAtRoot_UsedFromSrcTest_IsRegisteredForRecords()
    {
        TestArtifacts.SkipIfMissing();

        WriteManifest(_root, "5a3f0b11-3611-4a04-8004-000000003611", 63330, "SRAF Root Table");
        Touch(Path.Combine(_root, "Probe.Table.al"), """
        table 63330 "SRAF Root Probe"
        {
            DataClassification = SystemMetadata;
            fields
            {
                field(1; "Code"; Code[20]) { }
                field(2; "Value"; Integer) { }
            }
            keys { key(PK; "Code") { Clustered = true; } }
        }
        """);
        Touch(Path.Combine(_root, "src", "Tests.Codeunit.al"), """
        codeunit 63331 "SRAF Root Table Tests"
        {
            Subtype = Test;

            [Test]
            procedure RootTableRoundTrips()
            var
                Probe: Record "SRAF Root Probe";
            begin
                Probe.Init();
                Probe.Code := 'X';
                Probe.Value := 42;
                Probe.Insert();
                Clear(Probe);
                if not Probe.Get('X') then
                    Error('row X not found after Insert');
                if Probe.Value <> 42 then
                    Error('Value was %1, expected 42', Probe.Value);
            end;
        }
        """);

        var (output, exit) = RunRunner(_root);

        Assert.Equal(1, TestCount(output));
        AssertPassed(output, "RootTableRoundTrips");
        Assert.Equal(0, exit);
    }
}
