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
    /// Regression pin: an app that keeps everything under src/ still gets exactly [src] — the
    /// widening is reserved for suites that need it, so the emitter, the RecordPatches
    /// registration and the per-call cost of every existing src/ suite are untouched.
    /// (ComputeAlCacheKey hashes the .al files relative to their common directory, not this
    /// list, so the key would survive a widening anyway; the pin is about the paths.)
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

    /// <summary>
    /// The shape every real bundle has: a <c>.alpackages/</c> of symbol .app files beside
    /// <c>src/</c>. A sibling folder with no <c>.al</c> in it is not a reason to widen.
    /// </summary>
    [Fact]
    public void SiblingDirWithoutAl_DoesNotWidenThePaths()
    {
        Touch(Path.Combine(_root, "app.json"), "{}");
        Touch(Path.Combine(_root, ".alpackages", "Microsoft_Application_28.0.0.0.app"), "not al");
        Touch(Path.Combine(_root, "src", "A.al"));

        var paths = ProgramSupport.CollectSuitePaths(_root);

        Assert.Equal(new[] { Path.Combine(_root, "src") }, paths);
    }

    [Fact]
    public void AppDirOnly_StillReturnsExactlyTheAppDir()
    {
        Touch(Path.Combine(_root, "app.json"), "{}");
        Touch(Path.Combine(_root, "app", "A.al"));

        var paths = ProgramSupport.CollectSuitePaths(_root);

        Assert.Equal(new[] { Path.Combine(_root, "app") }, paths);
    }

    [Fact]
    public void TestDirOnly_StillReturnsExactlyTheTestDir()
    {
        Touch(Path.Combine(_root, "test", "A.al"));

        var paths = ProgramSupport.CollectSuitePaths(_root);

        Assert.Equal(new[] { Path.Combine(_root, "test") }, paths);
    }

    /// <summary>
    /// Legacy bucket layout with the suite AS the bucket root: <c>_shared/</c> holds AL and is
    /// appended by CollectSuitePaths itself, so it is not "outside" and must not widen.
    /// </summary>
    [Fact]
    public void SharedDirUnderTheSuite_IsCoveredNotOutside()
    {
        Touch(Path.Combine(_root, "app.json"), "{}");
        Touch(Path.Combine(_root, "src", "A.al"));
        Touch(Path.Combine(_root, "_shared", "Assert.al"));

        var paths = ProgramSupport.CollectSuitePaths(_root, bucketRoot: _root);

        Assert.Equal(new[] { Path.Combine(_root, "src"), Path.Combine(_root, "_shared") }, paths);
    }

    /// <summary>
    /// A dot-directory is never an AL source root and can be huge (.git). AL under one does not
    /// widen the suite; the predicate does not even walk it.
    /// </summary>
    [Fact]
    public void AlUnderADotDirectory_DoesNotWidenThePaths()
    {
        Touch(Path.Combine(_root, "app.json"), "{}");
        Touch(Path.Combine(_root, "src", "A.al"));
        Touch(Path.Combine(_root, ".git", "stray.al"));

        var paths = ProgramSupport.CollectSuitePaths(_root);

        Assert.Equal(new[] { Path.Combine(_root, "src") }, paths);
    }

    /// <summary>
    /// A suite's sub-directories are part of that suite by design, nested app.json or not
    /// (Suites.cs: "a suite's own sub-directories are part of that suite, never separate
    /// buckets"). A flat suite has always compiled such a child; a src/ suite now does the same
    /// instead of silently dropping it.
    /// </summary>
    [Fact]
    public void NestedAppInASiblingDir_IsPartOfThisSuite_LikeAFlatSuite()
    {
        Touch(Path.Combine(_root, "app.json"), "{}");
        Touch(Path.Combine(_root, "src", "A.al"));
        Touch(Path.Combine(_root, "fixtures", "child", "app.json"), "{}");
        Touch(Path.Combine(_root, "fixtures", "child", "src", "C.al"));

        var paths = ProgramSupport.CollectSuitePaths(_root);

        Assert.Equal(new[] { _root }, paths);
    }

    private static bool TryMakeUnreadable(string path)
    {
        try { File.SetUnixFileMode(path, UnixFileMode.None); }
        catch { return false; }
        try { Directory.GetDirectories(path); return false; }
        catch (UnauthorizedAccessException) { return true; }
        catch (IOException) { return true; }
    }

    /// <summary>
    /// A sibling the predicate cannot read is "could not tell", not "no AL there": the suite
    /// widens to its root rather than quietly returning [src] with a helper possibly hidden
    /// behind the permission bits. (Same third-state rule as the guards: an unmeasurable case
    /// must not resolve to the answer that runs fewer tests.)
    /// </summary>
    [SkippableFact]
    public void UnreadableSiblingDir_WidensToTheRoot()
    {
        Touch(Path.Combine(_root, "app.json"), "{}");
        Touch(Path.Combine(_root, "src", "A.al"));
        var locked = Path.Combine(_root, "locked");
        Directory.CreateDirectory(Path.Combine(locked, "inner"));
        Skip.IfNot(TryMakeUnreadable(locked), "permission bits do not bite here (Windows, or running as root)");
        try
        {
            var paths = ProgramSupport.CollectSuitePaths(_root);

            Assert.Equal(new[] { _root }, paths);
        }
        finally
        {
            try { File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            catch { }
        }
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

    // -- #3735: registration mirrors the compile, folder for folder ----------------------

    /// <summary>
    /// The unit half. <c>SuiteRegistrationDirs</c> is what Program.cs's two register-source-dirs
    /// loops hand <c>RecordPatches.AddSourceDirs</c>, and it must be the folder set the compile
    /// reads - not a second derivation of it. Concrete list, in order, for the layout that used
    /// to break: the old loops answered <c>[src]</c> here and dropped <c>app/</c>,
    /// <c>app2/</c> and <c>test/</c> on the floor.
    /// </summary>
    [Fact]
    public void SuiteRegistrationDirs_CoversEveryConventionalFolder_NotJustSrc()
    {
        Touch(Path.Combine(_root, "app.json"), "{}");
        Touch(Path.Combine(_root, "src", "A.al"));
        Touch(Path.Combine(_root, "app", "B.al"));
        Touch(Path.Combine(_root, "app2", "C.al"));
        Touch(Path.Combine(_root, "test", "D.al"));

        Assert.Equal(
            new[]
            {
                Path.Combine(_root, "src"), Path.Combine(_root, "app"),
                Path.Combine(_root, "app2"), Path.Combine(_root, "test"),
            },
            ProgramSupport.SuiteRegistrationDirs(_root));
    }

    /// <summary>
    /// The order is part of the answer, not an accident of the filesystem. The list is both the
    /// compile's path list and the <c>AddSourceDirs</c> registration list, and
    /// <c>Directory.EnumerateDirectories</c> returns <c>app*/</c> in filesystem order - NTFS
    /// sorted, ext4 not - which is why the concrete-list assertion above passed on Windows and
    /// failed on the BC 27.5 Linux leg. <c>app10/</c> before <c>app2/</c> is what makes this an
    /// ordinal sort rather than a numeric one. It cannot go RED on NTFS; the Linux legs are where
    /// it bites.
    /// </summary>
    [Fact]
    public void SuiteRegistrationDirs_OrdersAppFolders_Ordinally()
    {
        Touch(Path.Combine(_root, "app.json"), "{}");
        Touch(Path.Combine(_root, "src", "A.al"));
        Touch(Path.Combine(_root, "app2", "B.al"));
        Touch(Path.Combine(_root, "app10", "C.al"));
        Touch(Path.Combine(_root, "app", "D.al"));
        Touch(Path.Combine(_root, "test", "E.al"));

        Assert.Equal(
            new[]
            {
                Path.Combine(_root, "src"), Path.Combine(_root, "app"),
                Path.Combine(_root, "app10"), Path.Combine(_root, "app2"),
                Path.Combine(_root, "test"),
            },
            ProgramSupport.SuiteRegistrationDirs(_root));
    }

    /// <summary>
    /// A <c>test/</c>-only suite registered NOTHING before - the branch that fell through both
    /// the src/ arm and the flat-bundle arm - while the compile returned <c>[test]</c>.
    /// </summary>
    [Fact]
    public void SuiteRegistrationDirs_TestOnlySuite_RegistersTest()
    {
        Touch(Path.Combine(_root, "test", "A.al"));

        Assert.Equal(new[] { Path.Combine(_root, "test") },
            ProgramSupport.SuiteRegistrationDirs(_root));
    }

    /// <summary>
    /// The drift pin itself: over every layout this file exercises, the registered set IS the
    /// compiled set. A future editor who widens one and not the other fails here - which is the
    /// failure #3611/#3714 and #3735 each shipped once.
    /// <para>
    /// What it cannot catch: since the fix, <c>SuiteRegistrationDirs</c> IS
    /// <c>CollectSuitePaths</c>, so this compares one function against itself and would stay
    /// green through any change either makes - including the filesystem-dependent
    /// <c>app*/</c> order that reddened the BC 27.5 and 28.4 legs. It pins that the two never
    /// diverge again, not what either answers; the concrete-list tests above pin that.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("src")]
    [InlineData("test")]
    [InlineData("src+test")]
    [InlineData("src+app+app2+test")]
    [InlineData("flat")]
    [InlineData("root-al-beside-src")]
    [InlineData("sibling-dir-beside-src")]
    [InlineData("shared")]
    public void RegisteredDirs_AreExactlyTheCompiledDirs(string layout)
    {
        Touch(Path.Combine(_root, "app.json"), "{}");
        switch (layout)
        {
            case "src": Touch(Path.Combine(_root, "src", "A.al")); break;
            case "test": Touch(Path.Combine(_root, "test", "A.al")); break;
            case "src+test":
                Touch(Path.Combine(_root, "src", "A.al"));
                Touch(Path.Combine(_root, "test", "B.al"));
                break;
            case "src+app+app2+test":
                Touch(Path.Combine(_root, "src", "A.al"));
                Touch(Path.Combine(_root, "app", "B.al"));
                Touch(Path.Combine(_root, "app2", "C.al"));
                Touch(Path.Combine(_root, "test", "D.al"));
                break;
            case "flat": Touch(Path.Combine(_root, "A.al")); break;
            case "root-al-beside-src":
                Touch(Path.Combine(_root, "src", "A.al"));
                Touch(Path.Combine(_root, "B.al"));
                break;
            case "sibling-dir-beside-src":
                Touch(Path.Combine(_root, "src", "A.al"));
                Touch(Path.Combine(_root, "ControlAddin", "B.al"));
                break;
            case "shared":
                Touch(Path.Combine(_root, "src", "A.al"));
                Touch(Path.Combine(_root, "_shared", "Assert.al"));
                break;
            default: throw new ArgumentOutOfRangeException(nameof(layout), layout, null);
        }

        var bucketRoot = layout == "shared" ? _root : null;
        var compiled = ProgramSupport.CollectSuitePaths(_root, bucketRoot);

        Assert.NotEmpty(compiled);
        Assert.Equal(compiled, ProgramSupport.SuiteRegistrationDirs(_root, bucketRoot));
    }

    /// <summary>
    /// The end-to-end shape #3735 turned on: a PAGE declared under <c>test/</c> in a suite with
    /// no <c>src/</c>. It compiled, but nothing registered <c>test/</c>, so the page was absent
    /// from the runner's object inventory and <c>Page.RunModal</c> answered
    /// "An object with that ID does not exist in the current application" (measured on
    /// ea27ed9f). Past that, the third LiveNavTestPage route - RunnerTestClientSession.GetPage,
    /// via [ModalPageHandler], which applies no shape gate - would have reached
    /// BuiltInPageModeActionRule.RefuseUnknownPageType.
    /// <para>The assertion is the mode switch itself, before and after, not "did not throw":
    /// a Card opened modally starts editable, and its built-in View action makes the page
    /// already on screen read-only (corpus codeunit 60479 "TPMS Tests" measures that shape on a
    /// real service tier). An implementation that answered a default for either boolean fails.
    /// </para>
    /// </summary>
    [SkippableFact]
    public void PageUnderTestDir_IsDrivenLiveByAModalPageHandler()
    {
        TestArtifacts.SkipIfMissing();

        WriteManifest(_root, "7c1a0b22-3735-4b01-9001-000000003735", 63340, "SRAF Test Only");
        Touch(Path.Combine(_root, "test", "Objects.al"), ModeProbeObjects("H", 63341, 63342));
        Touch(Path.Combine(_root, "test", "Tests.al"), ModeProbeTests("H", 63343));

        var (output, exit) = RunRunner(_root);

        Assert.DoesNotContain("does not exist in the current application", output);
        Assert.Equal(1, TestCount(output));
        AssertPassed(output, "ModeActionResolvesForAPageDeclaredOutsideSrc");
        Assert.Equal(0, exit);
    }

    /// <summary>
    /// The same, one folder over: the page under <c>app2/</c> beside a <c>src/</c> that does
    /// exist - so the old loops registered <c>[src]</c> and dropped the page anyway.
    /// </summary>
    [SkippableFact]
    public void PageUnderAppDir_BesideSrc_IsDrivenLiveByAModalPageHandler()
    {
        TestArtifacts.SkipIfMissing();

        WriteManifest(_root, "7c1a0b22-3735-4b02-9002-000000003735", 63360, "SRAF App2 Page");
        Touch(Path.Combine(_root, "app2", "Objects.al"), ModeProbeObjects("A", 63361, 63362));
        Touch(Path.Combine(_root, "src", "Tests.al"), ModeProbeTests("A", 63363));

        var (output, exit) = RunRunner(_root);

        Assert.DoesNotContain("does not exist in the current application", output);
        Assert.Equal(1, TestCount(output));
        AssertPassed(output, "ModeActionResolvesForAPageDeclaredOutsideSrc");
        Assert.Equal(0, exit);
    }

    /// <summary>
    /// The table half of the same branch - the sibling of
    /// <see cref="TableAtRoot_UsedFromSrcTest_IsRegisteredForRecords"/> for a suite whose only
    /// folder is <c>test/</c>. Insert-then-Get of a non-default value, so a provider that
    /// answered zero rows or a defaulted field fails.
    /// </summary>
    [SkippableFact]
    public void TableUnderTestDir_IsRegisteredForRecords()
    {
        TestArtifacts.SkipIfMissing();

        WriteManifest(_root, "7c1a0b22-3735-4b03-9003-000000003735", 63370, "SRAF Test Table");
        Touch(Path.Combine(_root, "test", "Probe.Table.al"), """
        table 63370 "SRAF Test Probe"
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
        Touch(Path.Combine(_root, "test", "Tests.al"), """
        codeunit 63371 "SRAF Test Table Tests"
        {
            Subtype = Test;

            [Test]
            procedure TestDirTableRoundTrips()
            var
                Probe: Record "SRAF Test Probe";
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
        AssertPassed(output, "TestDirTableRoundTrips");
        Assert.Equal(0, exit);
    }

    // A SingleInstance probe plus a sourceless Card page. The probe is how a [ModalPageHandler]
    // reports what it saw: the handler is gone by the time the test resumes (corpus codeunit
    // 60473 "TPMS Open Probe" makes the same move). Raw Error(), never Codeunit Assert - the
    // toolkit would pull a Base Application floor (.claude/rules/no-base-app-in-csharp-tests.md).
    private static string ModeProbeObjects(string tag, int probeId, int pageId) => $$"""
        codeunit {{probeId}} "SRAF Probe {{tag}}"
        {
            SingleInstance = true;

            var
                Runs: Integer;
                Before: Boolean;
                After: Boolean;

            procedure Reset()
            begin
                Runs := 0;
                Before := false;
                After := false;
            end;

            procedure Note(EditableBefore: Boolean; EditableAfter: Boolean)
            begin
                Runs += 1;
                Before := EditableBefore;
                After := EditableAfter;
            end;

            procedure GetRuns(): Integer
            begin
                exit(Runs);
            end;

            procedure GetBefore(): Boolean
            begin
                exit(Before);
            end;

            procedure GetAfter(): Boolean
            begin
                exit(After);
            end;
        }

        page {{pageId}} "SRAF Mode Card {{tag}}"
        {
            PageType = Card;
            ApplicationArea = All;
        }
        """;

    private static string ModeProbeTests(string tag, int codeunitId) => $$"""
        codeunit {{codeunitId}} "SRAF Mode Tests {{tag}}"
        {
            Subtype = Test;

            [Test]
            [HandlerFunctions('ModeHandler')]
            procedure ModeActionResolvesForAPageDeclaredOutsideSrc()
            var
                Probe: Codeunit "SRAF Probe {{tag}}";
            begin
                Probe.Reset();
                Page.RunModal(Page::"SRAF Mode Card {{tag}}");
                if Probe.GetRuns() <> 1 then
                    Error('handler ran %1 time(s), expected 1', Probe.GetRuns());
                if not Probe.GetBefore() then
                    Error('a Card opened modally must start out editable');
                if Probe.GetAfter() then
                    Error('the built-in View action must make the open page read-only');
            end;

            [ModalPageHandler]
            procedure ModeHandler(var Card: TestPage "SRAF Mode Card {{tag}}")
            var
                Probe: Codeunit "SRAF Probe {{tag}}";
                EditableBefore: Boolean;
            begin
                EditableBefore := Card.Editable();
                // Before #3735 this raised RunnerOutOfScopeException - the page's PageType was
                // unknown, because nothing had parsed the folder it is declared in.
                Card.View().Invoke();
                Probe.Note(EditableBefore, Card.Editable());
            end;
        }
        """;
}
