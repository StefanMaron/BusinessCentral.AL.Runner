// Issue #2223: once the runner-owned `<artifacts>/<ver>/platform-apps` directory exists, the
// #2067 extraProvisionSearchDirs block folds it into packageCacheDirs UNCONDITIONALLY, so every
// invocation resolves and loads the Base Application closure — including bundles whose need comes
// from nothing but an `application` floor in app.json.
//
// Two things these tests pin, and the second is a regression the first revision of this fix
// shipped:
//
//   1. A bundle whose platform-app need comes only from its app.json floor resolves NO Microsoft
//      package on a warm, quiet run.
//   2. It does NOT do that by replaying a child's output on top of this process's own. The
//      attempt is skipped under --verbose and --output-json precisely because the replay would
//      duplicate the parent's preamble; CrossMajorNoteTests and OutputPathPreparationTests own
//      the far side of that boundary, and this file pins the boundary itself.
//
// The observable is AL_RUNNER_PHASE_LOG's `deps_resolved`, not the `[dep]` verbose lines the
// first revision used. That matters: `[dep]` only prints under --verbose, which is exactly the
// mode the feature now declines to act in, so a --verbose test here would assert about a path
// the user never takes. A wall clock is not used at all — it would be flaky by construction and
// silent about which mechanism moved.
//
// These fixtures declare an `application` floor, which
// .claude/rules/no-base-app-in-csharp-tests.md otherwise forbids, because that floor IS the
// subject: measured on `main`, a platform-only bundle resolves ONE package (Microsoft/System,
// ~620 KB) and an application-floor bundle resolves FIVE including the ~98 MB Base Application.
// Without the floor there is no closure to skip and nothing being tested. Same carve-out
// PlaceholderFloorProvisioningTests takes, for the same reason.
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public sealed class DeferredPlatformAppsWithholdTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private static string RealServiceTierDir()
    {
        var version = AlRunner.Infrastructure.BcArtifacts.EngineBuiltVersion()
            ?? throw new InvalidOperationException("EngineBuiltVersion() unavailable.");
        var home = Environment.GetEnvironmentVariable("HOME")
            ?? throw new InvalidOperationException("HOME not set on this machine.");
        return Path.Combine(TestArtifacts.StandardCacheDir(home), version.ToString());
    }

    /// <summary>
    /// A bundle declaring an `application` floor whose AL names nothing Microsoft — the shape
    /// this issue measures, and the one the warm skip is allowed to act on.
    /// </summary>
    private static string WriteFloorOnlyBundle(string dir, int id)
    {
        Directory.CreateDirectory(dir);
        WriteManifest(dir, id, "Withhold", dependencies: "");
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
    /// The same floor, but the AL genuinely reads a Microsoft platform object. The attempt cannot
    /// come out green, so the run must fall back and load the closure — the half that keeps this
    /// from trading a latency bug for a correctness one.
    /// </summary>
    private static string WriteBundleUsingSystemApp(string dir, int id)
    {
        Directory.CreateDirectory(dir);
        WriteManifest(dir, id, "Withhold Needs", dependencies: "");
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
    /// An EXPLICIT Microsoft dependency whose AL happens to use nothing from it. The AL alone
    /// cannot separate this from the floor-only bundle — both pass an attempt without the closure
    /// — so only the declared root distinguishes them.
    /// </summary>
    private static string WriteBundleWithExplicitMicrosoftDependency(string dir, int id)
    {
        Directory.CreateDirectory(dir);
        WriteManifest(dir, id, "Withhold Explicit", dependencies:
            """
            { "id": "437dbf0e-84ff-417a-965d-ed2bb9650972", "name": "Base Application", "publisher": "Microsoft", "version": "27.0.0.0" }
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

    private static void WriteManifest(string dir, int id, string name, string dependencies) =>
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "{{Guid.NewGuid()}}",
          "name": "{{name}} {{id}}",
          "publisher": "Repro2223",
          "version": "1.0.0.0",
          "dependencies": [{{dependencies}}],
          "idRanges": [ { "from": {{id}}, "to": {{id + 9}} } ],
          "platform": "27.0.0.0",
          "application": "27.0.0.0",
          "runtime": "14.0"
        }
        """);

    /// <summary>
    /// Builds an artifacts root holding a REAL runner-owned platform-apps directory for the
    /// engine's own version, by symlinking the machine's provisioned copy. That presence is the
    /// whole subject: it is what makes the fold resolve the closure.
    /// </summary>
    /// <param name="requiredApps">
    /// App names this test's bundle needs served from that directory. Not cosmetic: this box's
    /// engine version 28.1.49838.53910 ships a platform-apps holding System.app ALONE while its
    /// sibling ...54308 holds all six, so a guard asking only "is any .app present" let a
    /// Base-Application test run against a directory that could never serve it — and the
    /// provisioning refusal that produced read exactly like the defect under test. The third
    /// state here is a SKIP naming what is absent (guards-need-a-third-state.md).
    /// </param>
    private static string? BuildArtifactsRootWithPlatformApps(string scratchRoot, params string[] requiredApps)
    {
        var version = AlRunner.Infrastructure.BcArtifacts.EngineBuiltVersion();
        if (version == null) return null;
        var home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrEmpty(home)) return null;

        var realPlatformApps = Path.Combine(
            TestArtifacts.StandardCacheDir(home), version.ToString(), "platform-apps");
        if (!Directory.Exists(realPlatformApps)) return null;

        // Matched on the FILE NAME, the way the directory is laid out: `System.app` for the
        // platform-symbols app, `Microsoft_<Name>_<version>.app` for the rest.
        var present = Directory.EnumerateFiles(realPlatformApps, "*.app")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n != null)
            .ToList();
        bool Has(string app) => present.Any(n =>
            string.Equals(n, app, StringComparison.OrdinalIgnoreCase)
            || n!.StartsWith("Microsoft_" + app + "_", StringComparison.OrdinalIgnoreCase));
        foreach (var app in requiredApps.DefaultIfEmpty("Application"))
            if (!Has(app)) return null;

        var root = Path.Combine(scratchRoot, "artifacts");
        var versionDir = Path.Combine(root, version.ToString());
        Directory.CreateDirectory(versionDir);
        Directory.CreateSymbolicLink(Path.Combine(versionDir, "platform-apps"), realPlatformApps);
        return root;
    }

    private sealed record RunResult(string Output, int Exit, int DepsResolved, int DepAssembliesLoaded);

    /// <summary>
    /// Runs the bundle QUIETLY — no --verbose — with the runner-owned platform-apps directory
    /// visible through the artifacts root, and reads how many dependencies it resolved out of the
    /// phase log rather than out of verbose text. See the file header for why the observable
    /// cannot be the `[dep]` lines.
    /// </summary>
    private static RunResult Run(
        string bundleDir, string scratchRoot, string artifactsRoot, string tag, bool asDeferredChild = false)
    {
        var realServiceTierDir = RealServiceTierDir();
        TestArtifacts.SkipIf(!Directory.Exists(realServiceTierDir),
            $"real BC service-tier dir not provisioned at '{realServiceTierDir}'.");
        var isolatedHome = Path.Combine(scratchRoot, "home-" + tag);
        Directory.CreateDirectory(isolatedHome);
        var phaseLog = Path.Combine(scratchRoot, "phase-" + tag + ".jsonl");

        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append($" --artifact-path \"{realServiceTierDir}\"");
        args.Append($" \"{bundleDir}\"");
        args.Append($" --cache \"{Path.Combine(scratchRoot, "al-out-" + tag)}\"");
        // Never created: the ONLY platform-apps the run can see is the runner-owned directory
        // under the artifacts root below, which is exactly the fold under test.
        args.Append($" --package-cache \"{Path.Combine(isolatedHome, "no-such-package-cache")}\"");
        args.Append(" --no-auto-provision");

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
        psi.Environment["AL_RUNNER_PHASE_LOG"] = phaseLog;
        if (asDeferredChild)
            psi.Environment[AlRunner.Infrastructure.ProvisioningCheck.DeferredPlatformAppsEnvVar] = "1";
        else
            // An inherited value would make an "ordinary invocation" secretly be the attempt
            // child, which is the one thing these runs must not be.
            psi.Environment.Remove(AlRunner.Infrastructure.ProvisioningCheck.DeferredPlatformAppsEnvVar);

        var sb = new StringBuilder();
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(300_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        string output; lock (sb) output = sb.ToString();

        var (deps, asm) = ReadPeakDepCounts(phaseLog, output);
        return new RunResult(output, p.ExitCode, deps, asm);
    }

    /// <summary>
    /// The HIGHEST dep counts any process in the log reports. Deliberately a maximum rather than
    /// the last row: a run can involve several processes — the shadow re-exec parent, and on the
    /// fallback path the discarded attempt child — and the question these tests ask is whether the
    /// closure was loaded AT ALL, so a single zero-row process must not mask a sibling that
    /// loaded it.
    /// </summary>
    private static (int Deps, int Assemblies) ReadPeakDepCounts(string phaseLogPath, string output)
    {
        Assert.True(File.Exists(phaseLogPath),
            $"no phase log at '{phaseLogPath}' — the run cannot be measured.\n--- output ---\n{output}");
        var deps = 0;
        var asm = 0;
        var rows = 0;
        foreach (var line in File.ReadAllLines(phaseLogPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("kind", out var kind) || kind.GetString() != "process") continue;
            rows++;
            if (root.TryGetProperty("deps_resolved", out var d)) deps = Math.Max(deps, d.GetInt32());
            if (root.TryGetProperty("dep_assemblies_loaded", out var a)) asm = Math.Max(asm, a.GetInt32());
        }
        Assert.True(rows > 0,
            $"the phase log at '{phaseLogPath}' holds no `process` row, so nothing was measured.\n"
            + $"--- output ---\n{output}");
        return (deps, asm);
    }

    private static void WithScratch(string name, Action<string> body)
    {
        var scratchRoot = TestScratch.Dir(name);
        try { body(scratchRoot); }
        finally { try { Directory.Delete(scratchRoot, recursive: true); } catch { } }
    }

    /// <summary>
    /// #2223's subject: an ordinary, quiet invocation of a bundle whose platform-app need comes
    /// only from its app.json floor, with the platform apps already provisioned, loads none of the
    /// Microsoft closure. Before the fix this resolved five Microsoft packages on every single
    /// invocation — #2232's deferral cannot reach the case, because its precondition is a PENDING
    /// DOWNLOAD and nothing is missing once `al-runner provision` has run.
    /// </summary>
    [SkippableFact]
    public void FloorOnlyBundle_WithPlatformAppsOnDisk_LoadsNoneOfTheClosure()
    {
        TestArtifacts.SkipIfMissing();
        WithScratch("al-runner-2223-warm-skip", scratch =>
        {
            var artifactsRoot = BuildArtifactsRootWithPlatformApps(scratch, "Application", "Base Application");
            TestArtifacts.SkipIf(artifactsRoot == null,
                "the engine version's runner-owned platform-apps directory does not serve the Application "
                + "closure, so there is nothing for this run to skip and nothing to measure.");

            var bundle = WriteFloorOnlyBundle(Path.Combine(scratch, "bundle"), 61994);
            var r = Run(bundle, scratch, artifactsRoot!, "warmskip");

            Assert.True(r.DepsResolved == 0,
                $"a bundle whose platform-app need comes only from its app.json floor still resolved "
                + $"{r.DepsResolved} dependency(ies) on a warm run with the apps on disk (#2223).\n"
                + $"--- output ---\n{r.Output}");
            Assert.Equal(0, r.DepAssembliesLoaded);
            Assert.True(r.Exit == 0, $"the run must still pass. exit={r.Exit}\n{r.Output}");
            Assert.Contains("pass:        1", r.Output);
        });
    }

    /// <summary>
    /// The control, and the half that keeps the fix from becoming a correctness bug. A bundle
    /// whose AL genuinely uses a Microsoft platform object must still get the closure: the attempt
    /// without it cannot come out green, so its output is discarded and the run proceeds with the
    /// apps. Without this, "skip the closure" could be implemented unconditionally, which reds
    /// nothing else here and breaks every bundle that uses Base Application.
    /// </summary>
    [SkippableFact]
    public void BundleUsingTheSystemApp_StillLoadsTheClosure_AndPasses()
    {
        TestArtifacts.SkipIfMissing();
        WithScratch("al-runner-2223-control", scratch =>
        {
            var artifactsRoot = BuildArtifactsRootWithPlatformApps(scratch, "Application", "Base Application");
            TestArtifacts.SkipIf(artifactsRoot == null,
                "the engine version's runner-owned platform-apps directory does not serve the Application closure.");

            var bundle = WriteBundleUsingSystemApp(Path.Combine(scratch, "bundle"), 61992);
            var r = Run(bundle, scratch, artifactsRoot!, "control");

            Assert.True(r.DepsResolved > 0,
                "a bundle whose AL uses a Microsoft platform object must still resolve the closure; "
                + "skipping it for every bundle would break exactly this one (#2223).\n"
                + $"--- output ---\n{r.Output}");
            Assert.True(r.Exit == 0, $"the run must still pass. exit={r.Exit}\n{r.Output}");
            Assert.Contains("pass:        1", r.Output);
        });
    }

    /// <summary>
    /// The clause a "skip whenever the closure is present" shortcut would delete, and the only
    /// test that can see it. A bundle DECLARING Base Application keeps the closure even though its
    /// AL uses nothing from it — so the attempt comes out green and the verdict alone would
    /// happily skip. A declared dependency is a statement about the app's contract, not about the
    /// code path one run happened to take.
    /// </summary>
    [SkippableFact]
    public void ExplicitMicrosoftDependency_StillLoadsTheClosure_EvenWhenTheAlUsesNothing()
    {
        TestArtifacts.SkipIfMissing();
        WithScratch("al-runner-2223-explicit", scratch =>
        {
            var artifactsRoot = BuildArtifactsRootWithPlatformApps(scratch, "Application", "Base Application");
            TestArtifacts.SkipIf(artifactsRoot == null,
                "the engine version's runner-owned platform-apps directory does not serve Base Application, "
                + "which this bundle declares.");

            var bundle = WriteBundleWithExplicitMicrosoftDependency(Path.Combine(scratch, "bundle"), 61996);
            var r = Run(bundle, scratch, artifactsRoot!, "explicit");

            Assert.True(r.DepsResolved > 0,
                "a bundle that DECLARES a Microsoft dependency must still resolve the closure, whatever "
                + "an attempt without it reports: the declaration is the contract, and the AL this run "
                + $"executed is not evidence about the AL a later one will (#2223).\n--- output ---\n{r.Output}");
            Assert.True(r.Exit == 0, $"the run must still pass. exit={r.Exit}\n{r.Output}");
        });
    }

    /// <summary>
    /// The regression the first revision of this fix shipped, pinned from this side.
    ///
    /// The skip replays a captured child's output, and under --verbose this process has already
    /// printed a startup preamble the child re-emits in full — so every line of it would appear
    /// twice. Measured then: the #2210 cross-major note went from one occurrence to two, and
    /// `--output-json`'s stdout-is-only-JSON contract broke. The fix declines to act under
    /// --verbose at all, and this test is what stops that condition being quietly dropped as
    /// redundant later.
    ///
    /// Asserted on a line every verbose run emits exactly once, rather than on the note (which
    /// needs a cross-major mismatch this fixture does not have). CrossMajorNoteTests and
    /// OutputPathPreparationTests assert the user-visible consequences; this one asserts the
    /// mechanism, so a reader of this file can see why the --verbose condition exists.
    ///
    /// Scope, so this test is not read as more than it is: it pins the LOUD duplication, the
    /// kind that breaks an assertion. A quiet run still duplicates the bundle banner, because
    /// FlushDeferredStartupLines runs before either decision site and the banner is not gated on
    /// --verbose — that residue is #2232's and is tracked as #4481, not fixed here.
    /// </summary>
    [SkippableFact]
    public void Verbose_DoesNotReplayAChildsOutput_SoThePreambleIsNotDuplicated()
    {
        TestArtifacts.SkipIfMissing();
        WithScratch("al-runner-2223-verbose", scratch =>
        {
            var artifactsRoot = BuildArtifactsRootWithPlatformApps(scratch, "Application", "Base Application");
            TestArtifacts.SkipIf(artifactsRoot == null,
                "the engine version's runner-owned platform-apps directory does not serve the Application closure.");

            var bundle = WriteFloorOnlyBundle(Path.Combine(scratch, "bundle"), 61998);

            var realServiceTierDir = RealServiceTierDir();
            var isolatedHome = Path.Combine(scratch, "home-verbose");
            Directory.CreateDirectory(isolatedHome);
            var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
            args.Append($" --artifact-path \"{realServiceTierDir}\"");
            args.Append($" \"{bundle}\"");
            args.Append($" --cache \"{Path.Combine(scratch, "al-out-verbose")}\"");
            args.Append($" --package-cache \"{Path.Combine(isolatedHome, "no-such-package-cache")}\"");
            args.Append(" --no-auto-provision --verbose");

            var psi = new ProcessStartInfo
            {
                FileName = "dotnet", Arguments = args.ToString(),
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
            };
            psi.Environment["HOME"] = isolatedHome;
            psi.Environment["AL_RUNNER_ARTIFACTS_ROOT"] = artifactsRoot!;
            psi.Environment.Remove(AlRunner.Infrastructure.ProvisioningCheck.DeferredPlatformAppsEnvVar);

            var sb = new StringBuilder();
            using var p = Process.Start(psi)!;
            p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            if (!p.WaitForExit(300_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
            p.WaitForExit();
            string output; lock (sb) output = sb.ToString();

            // One line per bundle, emitted once per run under --verbose. A replay of a child's
            // capture on top of the parent's own output prints it a second time.
            var banner = output.Split('\n').Count(l => l.Contains("package caches (requested)", StringComparison.Ordinal));
            Assert.True(banner == 1,
                $"the startup preamble appeared {banner} time(s) under --verbose; a captured child's "
                + "output is being replayed on top of this process's own, which is the duplication "
                + "#2223's first revision shipped (it broke CrossMajorNoteTests and "
                + $"OutputPathPreparationTests).\n--- output ---\n{output}");
            Assert.True(p.ExitCode == 0, $"the run must still pass. exit={p.ExitCode}\n{output}");
        });
    }
}
