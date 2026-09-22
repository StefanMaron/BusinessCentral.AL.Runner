// Issue #2223: once the runner-owned `<artifacts>/<ver>/platform-apps` directory exists, the
// #2067 extraProvisionSearchDirs block folds it into packageCacheDirs UNCONDITIONALLY, so every
// invocation resolves and loads the Base Application / System Application closure — including
// the bundle shape #2232 already established does not need it.
//
// The sharpest consequence is that #2232's own deferral stops working as designed. Its env var
// (AL_RUNNER_DEFERRED_PLATFORM_APPS) suppresses only the DOWNLOAD decision, never the fold, so
// the "attempt without the platform apps" ran WITH them whenever they happened to be on disk —
// the one process in the codebase whose entire purpose is to answer "does this bundle work
// without the closure?" could not ask the question.
//
// These tests assert the OBSERVABLE — which dependencies the run resolves — never a wall clock.
// A timing assertion would be flaky by construction and would not say which mechanism moved.
//
// The fixtures declare only a `platform` floor, never `application`
// (.claude/rules/no-base-app-in-csharp-tests.md). A platform floor alone synthesises the
// Microsoft/System implicit root, which is enough to reach the fold this pins.
using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class DeferredPlatformAppsWithholdTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    /// <summary>A `[dep] Microsoft/...` verbose line names one resolved platform package.</summary>
    private const string PlatformDepMarker = "[dep] Microsoft/";

    private static string RealServiceTierDir()
    {
        var version = AlRunner.Infrastructure.BcArtifacts.EngineBuiltVersion()
            ?? throw new InvalidOperationException("EngineBuiltVersion() unavailable.");
        var home = Environment.GetEnvironmentVariable("HOME")
            ?? throw new InvalidOperationException("HOME not set on this machine.");
        return Path.Combine(TestArtifacts.StandardCacheDir(home), version.ToString());
    }

    /// <summary>
    /// A bundle with a real `platform` floor whose AL DOES name a Microsoft platform object
    /// (<c>Record AllObj</c>, supplied by the System app). The attempt without the closure cannot
    /// come out green for this shape, so the run must fall back and load it — the half that keeps
    /// the fix from trading a latency bug for a correctness one.
    /// </summary>
    private static string WriteBundleUsingSystemApp(string dir, int id)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "{{Guid.NewGuid()}}",
          "name": "Withhold Needs {{id}}",
          "publisher": "Repro2223",
          "version": "1.0.0.0",
          "dependencies": [],
          "idRanges": [ { "from": {{id}}, "to": {{id + 9}} } ],
          "platform": "27.0.0.0",
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Tests.Codeunit.al"), $$"""
        codeunit {{id}} "Wh Needs Tests {{id}}"
        {
            Subtype = Test;

            [Test]
            procedure TheTest()
            var
                Obj: Record AllObj;
            begin
                Obj.SetRange("Object Type", Obj."Object Type"::Table);
                if Obj.IsEmpty() then
                    Error('no tables visible');
            end;
        }
        """);
        return dir;
    }

    /// <summary>
    /// A bundle declaring an EXPLICIT Microsoft dependency (Base Application) whose AL happens to
    /// use nothing from it. The AL alone cannot separate this from the floor-only bundle — both
    /// pass an attempt without the closure — so only the declared root distinguishes them, which
    /// is what <c>NeedComesOnlyFromImplicitRoots</c> reads.
    /// </summary>
    private static string WriteBundleWithExplicitMicrosoftDependency(string dir, int id)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "{{Guid.NewGuid()}}",
          "name": "Withhold Explicit {{id}}",
          "publisher": "Repro2223",
          "version": "1.0.0.0",
          "dependencies": [
            { "id": "437dbf0e-84ff-417a-965d-ed2bb9650972", "name": "Base Application", "publisher": "Microsoft", "version": "27.0.0.0" }
          ],
          "idRanges": [ { "from": {{id}}, "to": {{id + 9}} } ],
          "platform": "27.0.0.0",
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Tests.Codeunit.al"), $$"""
        codeunit {{id}} "Wh Explicit Tests {{id}}"
        {
            Subtype = Test;

            [Test]
            procedure TheTest()
            var
                a: Integer;
            begin
                a := 2 + 2;
                if a <> 4 then
                    Error('bad');
            end;
        }
        """);
        return dir;
    }

    /// <summary>
    /// A bundle with a real `platform` floor whose AL names nothing Microsoft — #2232's shape,
    /// and the one this issue measures.
    /// </summary>
    private static string WriteBundle(string dir, int id)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "{{Guid.NewGuid()}}",
          "name": "Withhold {{id}}",
          "publisher": "Repro2223",
          "version": "1.0.0.0",
          "dependencies": [],
          "idRanges": [ { "from": {{id}}, "to": {{id + 9}} } ],
          "platform": "27.0.0.0",
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Tests.Codeunit.al"), $$"""
        table {{id}} "Wh Row {{id}}"
        {
            fields { field(1; "No."; Integer) { } field(2; Txt; Text[30]) { } }
            keys { key(PK; "No.") { Clustered = true; } }
        }

        codeunit {{id + 1}} "Wh Tests {{id}}"
        {
            Subtype = Test;

            [Test]
            procedure TheTest()
            var
                Row: Record "Wh Row {{id}}";
            begin
                Row."No." := 7; Row.Txt := 'seven'; Row.Insert(true);
                Row.Get(7);
                if Row.Txt <> 'seven' then
                    Error('expected seven, got %1', Row.Txt);
            end;
        }
        """);
        return dir;
    }

    /// <summary>
    /// Builds an artifacts root holding a REAL, COMPLETE runner-owned platform-apps directory for
    /// the engine's own version, by symlinking the machine's provisioned copy. That presence is
    /// the whole subject: it is what makes the fold resolve the closure.
    /// </summary>
    /// <param name="requiredApps">
    /// App names this test's bundle actually needs served from that directory. A `platform`-only
    /// floor needs just <c>System</c>; a bundle declaring Base Application needs that too.
    ///
    /// Not cosmetic: this box's engine version 28.1.49838.53910 ships a platform-apps directory
    /// holding System.app ALONE, while its sibling 28.1.49838.54308 holds all six. A guard that
    /// asked only "is any .app present" therefore let the Base-Application test run against a
    /// directory that could never serve it, and the provisioning refusal that produced read
    /// exactly like the defect under test. The third state here is a SKIP naming what is absent
    /// (.claude/rules/guards-need-a-third-state.md) — never a pass, and never a red.
    /// </param>
    private static string? BuildArtifactsRootWithPlatformApps(string scratchRoot, params string[] requiredApps)
    {
        var version = AlRunner.Infrastructure.BcArtifacts.EngineBuiltVersion();
        if (version == null) return null;
        var home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrEmpty(home)) return null;

        var realVersionDir = Path.Combine(TestArtifacts.StandardCacheDir(home), version.ToString());
        var realPlatformApps = Path.Combine(realVersionDir, "platform-apps");
        if (!Directory.Exists(realPlatformApps)) return null;

        // Matched on the FILE NAME, the same way the directory is laid out: `System.app` for the
        // platform-symbols app, `Microsoft_<Name>_<version>.app` for the rest.
        var present = Directory.EnumerateFiles(realPlatformApps, "*.app")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n != null)
            .ToList();
        bool Has(string app) => present.Any(n =>
            string.Equals(n, app, StringComparison.OrdinalIgnoreCase)
            || n!.StartsWith("Microsoft_" + app + "_", StringComparison.OrdinalIgnoreCase));
        foreach (var app in requiredApps.DefaultIfEmpty("System"))
            if (!Has(app)) return null;

        var root = Path.Combine(scratchRoot, "artifacts");
        var versionDir = Path.Combine(root, version.ToString());
        Directory.CreateDirectory(versionDir);
        Directory.CreateSymbolicLink(Path.Combine(versionDir, "platform-apps"), realPlatformApps);
        return root;
    }

    /// <summary>
    /// Runs the bundle with the runner-owned platform-apps directory visible through the
    /// artifacts root, as an ORDINARY invocation — no deferral env var. This is the shape every
    /// user invocation has on a machine that has run <c>al-runner provision</c>.
    /// </summary>
    private static (string Output, int Exit) RunOrdinary(
        string bundleDir, string scratchRoot, string artifactsRoot, string tag) =>
        RunWithArtifactsRoot(bundleDir, scratchRoot, artifactsRoot, tag, asDeferredChild: false);

    private static (string Output, int Exit) RunDeferredChild(
        string bundleDir, string scratchRoot, string artifactsRoot) =>
        RunWithArtifactsRoot(bundleDir, scratchRoot, artifactsRoot, "deferred", asDeferredChild: true);

    private static (string Output, int Exit) RunWithArtifactsRoot(
        string bundleDir, string scratchRoot, string artifactsRoot, string tag, bool asDeferredChild)
    {
        var realServiceTierDir = RealServiceTierDir();
        TestArtifacts.SkipIf(!Directory.Exists(realServiceTierDir),
            $"real BC service-tier dir not provisioned at '{realServiceTierDir}'.");
        var isolatedHome = Path.Combine(scratchRoot, "home-" + tag);
        Directory.CreateDirectory(isolatedHome);

        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append($" --artifact-path \"{realServiceTierDir}\"");
        args.Append($" \"{bundleDir}\"");
        args.Append($" --cache \"{Path.Combine(scratchRoot, "al-out-" + tag)}\"");
        // Never created: the ONLY platform-apps the run can see is the runner-owned directory
        // under the artifacts root below, which is exactly the fold under test.
        args.Append($" --package-cache \"{Path.Combine(isolatedHome, "no-such-package-cache")}\"");
        args.Append(" --no-auto-provision --verbose");

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
        psi.Environment["HOME"] = isolatedHome;
        psi.Environment["AL_RUNNER_ARTIFACTS_ROOT"] = artifactsRoot;
        if (asDeferredChild)
            // This process IS the attempt without the platform apps — the state #2232's parent
            // puts its child in. Setting it directly keeps that test to one process.
            psi.Environment[AlRunner.Infrastructure.ProvisioningCheck.DeferredPlatformAppsEnvVar] = "1";
        else
            // An inherited value would make an "ordinary invocation" secretly be the child,
            // which is the one thing these controls must not be.
            psi.Environment.Remove(AlRunner.Infrastructure.ProvisioningCheck.DeferredPlatformAppsEnvVar);

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

    private static void WithScratch(string name, Action<string> body)
    {
        var scratchRoot = TestScratch.Dir(name);
        try { body(scratchRoot); }
        finally { try { Directory.Delete(scratchRoot, recursive: true); } catch { } }
    }

    private static IReadOnlyList<string> PlatformDepLines(string output) =>
        output.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Contains(PlatformDepMarker, StringComparison.Ordinal))
            .ToList();

    /// <summary>
    /// The defect. #2232's attempt-without-the-platform-apps child, with a COMPLETE runner-owned
    /// platform-apps directory on disk, must resolve no Microsoft platform package at all —
    /// otherwise the attempt is not "without" them and its green verdict means nothing, while
    /// every ordinary invocation pays the same closure load this issue measures.
    ///
    /// Asserted on the resolved dependency list, which is the mechanism. A wall-clock assertion
    /// would pass or fail on machine load and would not name what moved.
    /// </summary>
    [SkippableFact]
    public void DeferredAttempt_WithPlatformAppsOnDisk_ResolvesNoMicrosoftPlatformPackage()
    {
        TestArtifacts.SkipIfMissing();
        WithScratch("al-runner-2223-withhold", scratch =>
        {
            var artifactsRoot = BuildArtifactsRootWithPlatformApps(scratch);
            TestArtifacts.SkipIf(artifactsRoot == null,
                "no complete runner-owned platform-apps directory is provisioned for the engine version; "
                + "this test's whole subject is the fold of that directory, so there is nothing to measure.");

            var bundle = WriteBundle(Path.Combine(scratch, "bundle"), 61990);
            var (output, exit) = RunDeferredChild(bundle, scratch, artifactsRoot!);

            var platformDeps = PlatformDepLines(output);
            Assert.True(platformDeps.Count == 0,
                "the deferred attempt resolved Microsoft platform package(s) from the runner-owned "
                + "platform-apps directory, so it did not run WITHOUT them (#2223):\n  "
                + string.Join("\n  ", platformDeps) + "\n--- full output ---\n" + output);
            Assert.True(exit == 0, $"the attempt must still come out green. exit={exit}\n{output}");
            Assert.Contains("pass:        1", output);
        });
    }

    /// <summary>
    /// The control for the withhold, and the half that keeps it from becoming a correctness bug.
    /// An ORDINARY invocation whose AL genuinely uses a Microsoft platform object must still get
    /// the closure. Without this, "withhold the directory" could be implemented as "never fold
    /// it", which reds nothing here and breaks every bundle that uses Base Application.
    ///
    /// Note which bundle this uses: the needs-closure one. A control written over the
    /// arithmetic bundle would pass under the warm skip too (the attempt comes out green and the
    /// closure is legitimately not loaded), so it could not tell the two apart.
    /// </summary>
    [SkippableFact]
    public void OrdinaryRun_BundleUsingTheSystemApp_StillResolvesTheMicrosoftClosure()
    {
        TestArtifacts.SkipIfMissing();
        WithScratch("al-runner-2223-control", scratch =>
        {
            var artifactsRoot = BuildArtifactsRootWithPlatformApps(scratch);
            TestArtifacts.SkipIf(artifactsRoot == null,
                "no complete runner-owned platform-apps directory is provisioned for the engine version.");

            var bundle = WriteBundleUsingSystemApp(Path.Combine(scratch, "bundle"), 61992);
            var (output, exit) = RunOrdinary(bundle, scratch, artifactsRoot!, "control");

            var platformDeps = PlatformDepLines(output);
            Assert.True(platformDeps.Count > 0,
                "an ordinary invocation whose AL uses a Microsoft platform object must still resolve "
                + "the closure; withholding it from every run would break exactly this bundle "
                + "(#2223).\n--- full output ---\n" + output);
            Assert.True(exit == 0, $"the run must still pass. exit={exit}\n{output}");
            Assert.Contains("pass:        1", output);
        });
    }

    /// <summary>
    /// #2223's own subject, one disk state on from #2232: an ORDINARY invocation of a bundle
    /// whose need comes only from an app.json floor, with the platform apps already provisioned,
    /// must not load the closure. Before the fix this resolved all five Microsoft packages on
    /// every single invocation — #2232's deferral cannot reach the case, because its precondition
    /// is a PENDING DOWNLOAD and nothing is missing once `al-runner provision` has run.
    ///
    /// Asserted on the resolved dependency list rather than a wall clock, deliberately: the
    /// measured 10.7 s -> 2.0 s belongs in the PR body as evidence, and a threshold assertion
    /// here would be flaky by construction and silent about which mechanism moved.
    /// </summary>
    [SkippableFact]
    public void OrdinaryRun_FloorOnlyBundle_WithPlatformAppsOnDisk_DoesNotLoadTheClosure()
    {
        TestArtifacts.SkipIfMissing();
        WithScratch("al-runner-2223-warm-skip", scratch =>
        {
            var artifactsRoot = BuildArtifactsRootWithPlatformApps(scratch);
            TestArtifacts.SkipIf(artifactsRoot == null,
                "no complete runner-owned platform-apps directory is provisioned for the engine version; "
                + "with nothing on disk this would measure #2232's cold path instead.");

            var bundle = WriteBundle(Path.Combine(scratch, "bundle"), 61994);
            var (output, exit) = RunOrdinary(bundle, scratch, artifactsRoot!, "warmskip");

            var platformDeps = PlatformDepLines(output);
            Assert.True(platformDeps.Count == 0,
                "a bundle whose platform-app need comes only from its app.json floor still loaded the "
                + "Microsoft closure on an ordinary invocation with the apps on disk (#2223):\n  "
                + string.Join("\n  ", platformDeps) + "\n--- full output ---\n" + output);
            Assert.True(exit == 0, $"the run must still pass. exit={exit}\n{output}");
            Assert.Contains("pass:        1", output);
        });
    }
    /// <summary>
    /// The clause a "skip whenever the closure is present" shortcut would delete, and the only
    /// test that can see it. A bundle DECLARING Base Application keeps the closure even though
    /// its AL uses nothing from it — so the attempt comes out green and the verdict alone would
    /// happily skip. Measured: with the implicit-roots check removed this bundle resolves 0
    /// Microsoft packages against 5 with it, while every other test in this file stays green.
    ///
    /// Why that matters beyond tidiness: a declared dependency is a statement about the app's
    /// contract, not about the code path one test run happened to take. Serving it without the
    /// package it names would make a later run that DOES reach that code fail for a reason the
    /// manifest already ruled out.
    /// </summary>
    [SkippableFact]
    public void OrdinaryRun_ExplicitMicrosoftDependency_StillResolvesTheClosure_EvenWhenTheAlUsesNothing()
    {
        TestArtifacts.SkipIfMissing();
        WithScratch("al-runner-2223-explicit", scratch =>
        {
            var artifactsRoot = BuildArtifactsRootWithPlatformApps(scratch, "System", "Base Application");
            TestArtifacts.SkipIf(artifactsRoot == null,
                "the engine version's runner-owned platform-apps directory does not serve Base Application, "
                + "which this bundle declares; with it absent the run refuses for a provisioning reason "
                + "rather than answering this test's question.");

            var bundle = WriteBundleWithExplicitMicrosoftDependency(Path.Combine(scratch, "bundle"), 61996);
            var (output, exit) = RunOrdinary(bundle, scratch, artifactsRoot!, "explicit");

            var platformDeps = PlatformDepLines(output);
            Assert.True(platformDeps.Count > 0,
                "a bundle that DECLARES a Microsoft dependency must still resolve the closure, whatever "
                + "an attempt without it reports: the declaration is the contract, and the AL this run "
                + "executed is not evidence about the AL a later one will (#2223).\n--- full output ---\n"
                + output);
            Assert.True(exit == 0, $"the run must still pass. exit={exit}\n{output}");
            Assert.Contains("pass:        1", output);
        });
    }
}
