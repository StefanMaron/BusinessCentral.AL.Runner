// PhaseLogIntegrationTests — end-to-end proof that AL_RUNNER_PHASE_LOG is actually
// WIRED to the runner's measurements, not just formattable (issue #1825).
//
// PhaseLogTests pins the record format and the append semantics in isolation. That
// is not enough: a phase log whose every field is formatted perfectly and populated
// with zeros would pass all of it, and the CI aggregate built on top would report a
// runner that boots instantly, compiles nothing and resolves no dependencies.
//
// So this suite runs the REAL runner over two fixture bundles chosen to sit on
// opposite sides of the question #1825 exists to answer:
//
//   bundle 1 — no `application` / `platform` / `dependencies`  → the "0 deps" cohort
//   bundle 2 — declares `application` + `platform`             → the "deps loaded" cohort
//
// and asserts the emitted records tell those two apart, in order, with non-default
// values for every field the aggregate reads.
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public sealed class PhaseLogIntegrationTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private readonly string _root;
    private readonly string _noDeps;
    private readonly string _withDeps;
    private readonly string _logPath;

    public PhaseLogIntegrationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "al-runner-phaselog-e2e", Guid.NewGuid().ToString("N"));
        _noDeps = Path.Combine(_root, "nodeps");
        _withDeps = Path.Combine(_root, "withdeps");
        _logPath = Path.Combine(_root, "logs", "phases.jsonl");
        Directory.CreateDirectory(_noDeps);
        Directory.CreateDirectory(_withDeps);
        // The nonce makes each run's AL source unique, so the AL-output cache always
        // MISSes and emit_ms/compile_ms are always real work. Without it the second
        // run of this suite on a machine would HIT, both would legitimately be 0, and
        // the assertions below would be flaky rather than proving.
        var nonce = Guid.NewGuid().ToString("N");
        WriteFixture(_noDeps, "PL NoDeps", "9b2e4c31-6d5a-4f18-8f2b-1c0a7e35d901", 62130, platformRoots: false, nonce);
        WriteFixture(_withDeps, "PL WithDeps", "3f7c1a08-24be-4d6f-9a51-b8e2d40c7f13", 62140, platformRoots: true, nonce);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>
    /// A minimal, self-contained one-suite AL package. <paramref name="platformRoots"/>
    /// controls the ONLY difference that matters here: whether the manifest carries
    /// `application`/`platform`, which is what makes AppLoader synthesise the implicit
    /// Microsoft roots and therefore what puts the bundle in the "deps loaded" cohort.
    /// </summary>
    private static void WriteFixture(string dir, string name, string id, int idFrom, bool platformRoots, string nonce)
    {
        var roots = platformRoots
            ? """
                "platform": "1.0.0.0",
              """
            : "";
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "{{id}}",
          "name": "{{name}}",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
        {{roots}}
          "idRanges": [ { "from": {{idFrom}}, "to": {{idFrom + 9}} } ],
          "runtime": "14.0"
        }
        """);

        File.WriteAllText(Path.Combine(dir, "Assert.Codeunit.al"), $$"""
        codeunit {{idFrom + 1}} "PL Assert {{idFrom}}"
        {
            procedure AreEqual(Expected: Integer; Actual: Integer; Msg: Text)
            begin
                if Expected <> Actual then
                    Error('Expected:<%1> Actual:<%2> %3', Expected, Actual, Msg);
            end;
        }
        """);

        File.WriteAllText(Path.Combine(dir, "Tests.Codeunit.al"), $$"""
        codeunit {{idFrom + 2}} "PL Tests {{idFrom}}"
        {
            Subtype = Test;

            var
                Assert: Codeunit "PL Assert {{idFrom}}";
                Nonce: Label '{{nonce}}';

            [Test]
            procedure Arithmetic()
            begin
                Assert.AreEqual(7, 3 + 4, 'sum');
                Assert.AreEqual({{nonce.Length}}, StrLen(Nonce), 'nonce');
            end;
        }
        """);
    }

    /// <summary>Runs the real runner over the given bundle directories.</summary>
    private (string Output, int Exit) RunBundles(string? phaseLogPath, params string[] bundles)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        var platformApps = Path.Combine(TestArtifacts.HomeDir() ?? "", ".al-runner", "platform-apps");
        if (Directory.Exists(platformApps)) args.Append($" --package-cache \"{platformApps}\"");
        foreach (var b in bundles) args.Append($" \"{b}\"");
        return Spawn(phaseLogPath, args.ToString());
    }

    /// <summary>
    /// Runs the runner with exactly the given CLI arguments and nothing else. Used for
    /// `--version`, which the parser only recognises as args[0]; prefixing it with
    /// --bc-version/--package-cache would make it a bundle path and exit 2.
    /// </summary>
    private (string Output, int Exit) RunRaw(string? phaseLogPath, string rawArgs) =>
        Spawn(phaseLogPath, TestBuildConfig.RunArgs(ProjectPath) + " " + rawArgs);

    private static (string Output, int Exit) Spawn(string? phaseLogPath, string argLine)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = argLine,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoRoot,
        };
        // Deliberately set/unset per invocation: "no env var → no file at all" is half
        // of what this suite proves, and inheriting a stray value would hide it.
        if (phaseLogPath != null) psi.Environment["AL_RUNNER_PHASE_LOG"] = phaseLogPath;
        else psi.Environment.Remove("AL_RUNNER_PHASE_LOG");

        // Verbose so the re-exec markers are observable. AlRunner/Log.cs installs a
        // FilteredWriter that drops `[Component]`-tagged lines at default verbosity;
        // `[r2r] re-execing` is printed BEFORE Log.Install() and survives, and the
        // re-exec explanations below (#2034: retagged `[reexec]`, exempted like `[bc]`)
        // now also survive at default verbosity on their own — but this stays verbose
        // regardless so the marker-derived expected-parent count never depends on which
        // tags happen to be exempted today.
        psi.Environment["AL_RUNNER_VERBOSE"] = "1";

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

    private static List<JsonElement> ReadRecords(string path, string kind) =>
        File.ReadAllLines(path)
            .Where(l => l.Length > 0)
            .Select(l => JsonDocument.Parse(l).RootElement)
            .Where(e => e.GetProperty("kind").GetString() == kind)
            .ToList();

    /// <summary>
    /// The whole instrument, end to end, on a real two-bundle run.
    ///
    /// Every assertion below fails against a phase log that is merely well-formed:
    /// the ordering fields must reconstruct the bundle sequence (that is how a
    /// quadratic per-bundle term would be spotted), the phase times must be the
    /// runner's real measurements, the engine-boot time must be the real
    /// `BC runtime patches applied` figure, and the two cohorts must be
    /// distinguishable by `deps_resolved`.
    /// </summary>
    [SkippableFact]
    public void RealRun_EmitsOrderedPerBundleRecordsAndOneProcessRecord()
    {
        TestArtifacts.SkipIfMissing();

        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var (output, exit) = RunBundles(_logPath, _noDeps, _withDeps);
        var after = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Assert.Equal(0, exit);
        Assert.True(File.Exists(_logPath), $"no phase log written. Runner output:\n{output}");

        var bundleRows = ReadRecords(_logPath, "bundle");
        var processRows = ReadRecords(_logPath, "process");

        // Exactly one process record; exactly one record per bundle, in argument order.
        Assert.Single(processRows);
        Assert.Equal(2, bundleRows.Count);
        Assert.Equal(new[] { 1, 2 }, bundleRows.Select(r => r.GetProperty("bundle_index").GetInt32()));
        Assert.All(bundleRows, r => Assert.Equal(2, r.GetProperty("bundles_in_process").GetInt32()));
        Assert.EndsWith("nodeps", bundleRows[0].GetProperty("bundle").GetString());
        Assert.EndsWith("withdeps", bundleRows[1].GetProperty("bundle").GetString());

        // The cohort split — the field #1825 is actually for. A manifest without
        // application/platform resolves nothing; one with them pulls the Microsoft
        // roots. Same process, same machine, back to back.
        Assert.Equal(0, bundleRows[0].GetProperty("deps_resolved").GetInt32());
        Assert.Equal(0, bundleRows[0].GetProperty("dep_assemblies_loaded").GetInt32());
        Assert.True(bundleRows[1].GetProperty("deps_resolved").GetInt32() > 0,
            "the application/platform bundle resolved no dependencies — deps_resolved is not wired");

        foreach (var r in bundleRows)
        {
            var emit = r.GetProperty("emit_ms").GetInt64();
            var compile = r.GetProperty("compile_ms").GetInt64();
            var wall = r.GetProperty("wall_ms").GetInt64();
            Assert.True(emit > 0, $"emit_ms not wired: {r}");
            Assert.True(compile > 0, $"compile_ms not wired: {r}");
            // Per-bundle wall covers its own phases plus the per-bundle overhead
            // (dep resolution, module registration) this instrument is hunting.
            Assert.True(wall >= emit + compile + r.GetProperty("run_ms").GetInt64(),
                $"wall_ms ({wall}) does not cover its own phases: {r}");
            // Exactly one AL-output cache decision per single-suite bundle, and the
            // two counters are mutually exclusive for it.
            Assert.Equal(1, r.GetProperty("cache_hits").GetInt32() + r.GetProperty("cache_misses").GetInt32());
        }

        // One app row per emitted module. Each fixture is a single app, so there is one
        // app row per bundle here — but the row must still carry the finer coordinates,
        // because the CI runner-extras step packs many app groups (23 as of #1847's
        // consolidation, was 38 before it) into ONE bundle and this is the only level
        // at which those app groups are separately visible.
        var appRows = ReadRecords(_logPath, "app");
        Assert.Equal(2, appRows.Count);
        // Named by the emitted module, which in bundled mode is the app.json name —
        // so a row is attributable to a specific app, not just to an ordinal.
        Assert.Equal(new[] { "PL NoDeps", "PL WithDeps" },
            appRows.Select(r => r.GetProperty("app").GetString()));
        Assert.All(appRows, r =>
        {
            Assert.Equal(1, r.GetProperty("app_index").GetInt32());
            Assert.Equal(1, r.GetProperty("apps_in_bundle").GetInt32());
            Assert.True(r.GetProperty("emit_ms").GetInt64() > 0, $"app emit_ms not wired: {r}");
            Assert.True(r.GetProperty("compile_ms").GetInt64() > 0, $"app compile_ms not wired: {r}");
            Assert.True(r.GetProperty("run_ms").GetInt64() > 0, $"app run_ms not wired: {r}");
            Assert.Equal(1, r.GetProperty("cache_misses").GetInt32());
        });
        // The app rows carry their bundle's coordinates and its resolved dep closure,
        // so the cohort split is answerable at app granularity too.
        Assert.Equal(new[] { 1, 2 }, appRows.Select(r => r.GetProperty("bundle_index").GetInt32()));
        Assert.Equal(0, appRows[0].GetProperty("deps_resolved").GetInt32());
        Assert.Equal(bundleRows[1].GetProperty("deps_resolved").GetInt32(),
            appRows[1].GetProperty("deps_resolved").GetInt32());
        // An app's phases roll up into its bundle's.
        for (var i = 0; i < 2; i++)
            Assert.Equal(bundleRows[i].GetProperty("emit_ms").GetInt64(),
                appRows[i].GetProperty("emit_ms").GetInt64());

        AssertBundleStagesAccountForTheBundleTurn(bundleRows, appRows);
        AssertAppStagesAccountForTheRunTurn(appRows);

        var proc = processRows[0];
        Assert.Equal(2, proc.GetProperty("bundles_in_process").GetInt32());
        Assert.Equal(0, proc.GetProperty("exit_code").GetInt32());
        Assert.True(proc.GetProperty("patches_ms").GetInt64() > 0,
            "patches_ms is zero — the BC engine boot measurement is not wired");
        Assert.True(proc.GetProperty("peak_rss_bytes").GetInt64() > 32L * 1024 * 1024,
            "peak_rss_bytes looks stubbed");
        // Process totals are the sum of the per-bundle rows, so the aggregate can
        // cross-check one against the other.
        Assert.Equal(bundleRows.Sum(r => r.GetProperty("emit_ms").GetInt64()),
            proc.GetProperty("emit_ms").GetInt64());
        Assert.Equal(bundleRows.Sum(r => r.GetProperty("deps_resolved").GetInt32()),
            proc.GetProperty("deps_resolved").GetInt32());
        // Process wall clock is measured from OS process start, so it covers every
        // bundle plus the boot and startup the residual is computed from.
        Assert.True(proc.GetProperty("wall_ms").GetInt64()
            > bundleRows.Sum(r => r.GetProperty("wall_ms").GetInt64()),
            "process wall_ms does not exceed the sum of its bundles — it is not measured from process start");
        // All bundle rows and the process row come from this one process.
        Assert.All(bundleRows, r =>
            Assert.Equal(proc.GetProperty("pid").GetInt32(), r.GetProperty("pid").GetInt32()));

        // One "runner spawn" is not necessarily one OS process. The runner re-execs itself
        // after a fresh Cecil rewrite of Ncl.dll, so a COLD spawn is two processes, each
        // waiting on the next. Every outer process's wall clock contains its child's, so
        // summing them all under kind=="process" would multiply every total the aggregate
        // reports. They are kept, under a distinct kind, because outer − inner is the real
        // cost of an extra process start — a per-spawn tax worth sizing, not worth hiding.
        //
        // Asserted against the re-exec markers the runner prints rather than a fixed
        // count: EVERY re-exec path must be labelled, and this fails if a new one is
        // added without labelling it, or an existing label is dropped.
        //
        // There used to be a second, unconditional re-exec here (the [r2r] branch, which
        // restarted the process with DOTNET_ReadyToRun=0). It is gone, so on a warm
        // ncl-cecil cache a spawn is legitimately ONE process and both sides of this
        // equality are 0. That is why the old `expectedParents >= 1` guard — which existed
        // to stop the equality being vacuous, and relied on the [r2r] branch firing every
        // time — could not be kept. The labelled-re-exec invariant it protected is now
        // covered directly by StartupJitModeTests, which asserts the [r2r] marker is absent
        // AND the run still passes, so a silently-reintroduced unlabelled re-exec fails
        // there rather than passing quietly here.
        var expectedParents =
            CountOccurrences(output, "[r2r] re-execing")
            + CountOccurrences(output, "[reexec] Fresh rewrite done")
            // The tool package no longer ships Microsoft.Dynamics.Nav.Ncl.dll (see
            // check-nupkg-contents.sh) — NclShadowRuntime re-execs into a shadow runtime
            // dir that legitimately has it, UNCONDITIONALLY (every cold AND warm spawn,
            // not just a fresh-rewrite cache MISS), so this fires on every test run here.
            + CountOccurrences(output, "[reexec] Ncl.dll not shipped in this install");
        var parents = ReadRecords(_logPath, "process-reexec-parent");
        Assert.Equal(expectedParents, parents.Count);
        Assert.All(parents, parent =>
        {
            Assert.NotEqual(proc.GetProperty("pid").GetInt32(), parent.GetProperty("pid").GetInt32());
            Assert.True(parent.GetProperty("wall_ms").GetInt64() > proc.GetProperty("wall_ms").GetInt64(),
                "a re-exec parent outlives its child by construction — it waits for it");
            // None of them reached bundle work, so none may carry phase numbers.
            Assert.Equal(0, parent.GetProperty("patches_ms").GetInt64());
            Assert.Equal(0, parent.GetProperty("bundles_in_process").GetInt32());
            Assert.Equal(0, parent.GetProperty("emit_ms").GetInt64());
        });

        // ── start_ms: the field that turns durations into an occupancy timeline (#1829).
        //
        // Summed wall clock over a step tells you how much work happened, never when the
        // workers were idle. (start_ms, start_ms + wall_ms) is an interval, and a set of
        // intervals is a timeline — which is how "1.83x achieved concurrency" was resolved
        // into "saturated for two thirds of the run, then single-threaded". None of that
        // arithmetic survives a stamp that is defaulted, per-row-arbitrary, or on a
        // different clock per process, so all three are pinned here on a real run.
        //
        // The window carries a 2 s slack because a process row's start_ms comes from
        // Process.StartTime, which on Linux is derived from the jiffy-granular
        // /proc/<pid>/stat starttime plus an estimated boot time and lands a few ms either
        // side of the true value. 2 s is still six orders of magnitude away from the
        // failure this catches — an unstamped row reading 0, i.e. January 1970.
        const long clockSlackMs = 2000;
        var allRows = bundleRows.Concat(appRows).Concat(processRows).Concat(parents).ToList();
        Assert.All(allRows, r =>
            Assert.InRange(r.GetProperty("start_ms").GetInt64(), before - clockSlackMs, after + clockSlackMs));

        // Bundles run one after another inside a process, so bundle 2 starts no earlier
        // than bundle 1 ends. This is the property that makes overlapping intervals mean
        // real concurrency rather than clock skew.
        var b0Start = bundleRows[0].GetProperty("start_ms").GetInt64();
        var b1Start = bundleRows[1].GetProperty("start_ms").GetInt64();
        Assert.True(b1Start >= b0Start + bundleRows[0].GetProperty("wall_ms").GetInt64(),
            $"bundle 2 starts at {b1Start} before bundle 1 ends — start_ms is not a real clock reading");
        // An app row opens inside its bundle's turn, never before it.
        Assert.True(appRows[0].GetProperty("start_ms").GetInt64() >= b0Start,
            "app row starts before the bundle that contains it");
        // The process row is stamped from OS process start, so it precedes all its work,
        // and every re-exec parent precedes the child it spawned and waited on.
        Assert.True(proc.GetProperty("start_ms").GetInt64() < b0Start,
            "process start_ms is not measured from OS process start");
        Assert.All(parents, parent =>
            Assert.True(parent.GetProperty("start_ms").GetInt64() <= proc.GetProperty("start_ms").GetInt64(),
                "a re-exec parent must start before the child it spawned"));
    }

    /// <summary>
    /// #1828: the bundle's turn is `Σ app groups + named stages`, and nothing else.
    ///
    /// Before this, `bundle wall − Σ app wall` was one opaque 152.3 s block on the CI
    /// runner-extras leg (43% of the step) that the instrument could not describe. The
    /// claim under test is not "some stages exist" — it is that the stages ACCOUNT for
    /// the block: they are non-overlapping (so their sum plus the app groups cannot
    /// exceed the bundle's wall clock), and they leave almost nothing unexplained.
    /// </summary>
    private static void AssertBundleStagesAccountForTheBundleTurn(
        List<JsonElement> bundleRows, List<JsonElement> appRows)
    {
        static Dictionary<string, long> Stages(JsonElement bundle)
        {
            Assert.True(bundle.TryGetProperty("stages", out var s),
                $"bundle row carries no stage breakdown — the #1828 marks are not wired: {bundle}");
            return s.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt64());
        }

        for (var i = 0; i < bundleRows.Count; i++)
        {
            var stages = Stages(bundleRows[i]);
            // Every bundle walks these four, whatever it declares. Named individually
            // rather than counted, so deleting a mark fails here instead of quietly
            // moving its time back into the unattributed remainder.
            foreach (var required in new[]
                     { "dep-resolve", "enumerate-suites", "build-app-groups", "wire-field-triggers" })
                Assert.True(stages.ContainsKey(required),
                    $"stage '{required}' missing from bundle {i + 1}: {string.Join(", ", stages.Keys)}");

            var bundleWall = bundleRows[i].GetProperty("wall_ms").GetInt64();
            var appWall = appRows
                .Where(a => a.GetProperty("bundle_index").GetInt32()
                            == bundleRows[i].GetProperty("bundle_index").GetInt32())
                .Sum(a => a.GetProperty("wall_ms").GetInt64());
            var staged = stages.Values.Sum();

            // No stage may nest inside another and no stage may overlap an app group:
            // either would make the parts sum past the whole, which is precisely the
            // failure that would send the next reader chasing overhead that isn't there.
            Assert.True(staged + appWall <= bundleWall + 50,
                $"stages ({staged}ms) + app groups ({appWall}ms) exceed the bundle's wall clock "
                + $"({bundleWall}ms) — a stage is double-counting: {bundleRows[i]}");

            // And the attribution is near-complete: whatever the marks miss shows up
            // here. Measured at 0.06 s of a 401 s runner-extras bundle; these fixtures
            // are ~1 s bundles, so the allowance is absolute, not proportional.
            var unattributed = bundleWall - appWall - staged;
            Assert.True(unattributed <= 750,
                $"{unattributed}ms of bundle {i + 1}'s turn is attributed to nothing "
                + $"(wall {bundleWall}ms, apps {appWall}ms, stages {staged}ms) — add a stage mark");
        }

        // The cohort split, at stage granularity — this is the finding #1828 asked for.
        // Loading dependencies is what the block mostly IS, so a bundle that declares
        // none must show no dep-load stage at all, and one that declares them must.
        var noDeps = Stages(bundleRows[0]).Keys.Where(k => k.StartsWith("dep-load:", StringComparison.Ordinal));
        Assert.Empty(noDeps);
        var withDeps = Stages(bundleRows[1])
            .Where(kv => kv.Key.StartsWith("dep-load:", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(withDeps);
        // Named per dependency, not lumped: one expensive dependency and a dozen cheap
        // ones are different problems, and only the per-dep split tells them apart.
        Assert.Contains(withDeps, kv => kv.Key.Length > "dep-load:".Length);
    }

    /// <summary>
    /// #1861: an app row's `run_ms` is `Σ named stages + whatever they miss`, mirroring
    /// #1828's bundle-level claim one level down.
    ///
    /// #1861 measured `run_ms − Σ reported test duration` at ~4.8s per app group on CI,
    /// essentially constant across 23 wildly different app groups (110.5s of a 128.8s
    /// "test run" phase, 51% of the whole runner-extras step) — a floor being paid per
    /// group, not workload. Before this, that floor was invisible inside one opaque
    /// `AddAppRun` span. The claim under test is that the marks ACCOUNT for it: every
    /// app row this fixture produces carries the full named breakdown, and — like the
    /// bundle-level check — the sum leaves almost nothing unattributed.
    /// </summary>
    private static void AssertAppStagesAccountForTheRunTurn(List<JsonElement> appRows)
    {
        foreach (var app in appRows)
        {
            Assert.True(app.TryGetProperty("stages", out var s),
                $"app row carries no stage breakdown — the #1861 marks are not wired: {app}");
            var stages = s.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt64());

            // Every app group walks these, whatever it declares. Named individually
            // rather than counted, so deleting a mark fails here instead of quietly
            // moving its time back into the unattributed remainder.
            foreach (var required in new[]
                     {
                         "set-test-assembly", "type-discovery",
                         // The #1861 follow-up review split the single opaque "install-seed"
                         // mark (85.1% of run_ms in the original PR's own measurement) into
                         // one mark per call, so a follow-up fix knows which of the six to
                         // chase instead of re-running this whole attribution exercise.
                         // #1867 replaced "install-seed-run-install-triggers" +
                         // "install-seed-ensure-company-initialized" (together ~82.5% of
                         // run_ms, the #1861 breakdown's own finding) with a single
                         // "install-seed-dep-company-baseline" mark that is either a cache
                         // restore or a fresh compute-and-cache — see TestExecutor.Run and
                         // AlRunner.Tests.InstallSeedDepCompanyCacheTests — plus a distinct
                         // "install-seed-run-own-install-triggers" mark for the bundle's own
                         // (always fresh, never cached) Install triggers.
                         "install-seed-reset-per-test", "install-seed-reset-for-new-bundle",
                         "install-seed-set-test-assembly", "install-seed-dep-company-baseline",
                         "install-seed-run-own-install-triggers", "install-seed-capture-baseline",
                         "codeunit-scan",
                         "event-subscriber-inject", "codeunit-reset", "codeunit-instantiate",
                         "resolve-display-name", "run-test-methods", "codeunit-dispose",
                     })
                Assert.True(stages.ContainsKey(required),
                    $"app stage '{required}' missing: {string.Join(", ", stages.Keys)}");

            var runMs = app.GetProperty("run_ms").GetInt64();
            var staged = stages.Values.Sum();

            // No stage may exceed the run turn it decomposes — that would mean a stage
            // is double-counting time (e.g. nesting inside another mark).
            Assert.True(staged <= runMs + 50,
                $"app stages ({staged}ms) exceed run_ms ({runMs}ms) — a stage is double-counting: {app}");

            // And the attribution is near-complete: whatever the marks miss shows up
            // here. These fixtures run one [Test] each on a near-empty install baseline,
            // so both run_ms and any residual are small; the allowance is absolute.
            var unattributed = runMs - staged;
            Assert.True(unattributed <= 250,
                $"{unattributed}ms of app run_ms is attributed to nothing "
                + $"(run_ms {runMs}ms, stages {staged}ms) — add a stage mark: {app}");
        }
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var n = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            n++;
        return n;
    }

    /// <summary>
    /// Negative direction: with the variable unset the runner writes nothing at all.
    /// `--version` returns before any BC type loads, so this is a sub-second spawn.
    /// </summary>
    [Fact]
    public void WithoutTheEnvVar_NoPhaseLogIsWritten()
    {
        var (output, exit) = RunRaw(phaseLogPath: null, "--version");
        Assert.Equal(0, exit);
        Assert.Contains("al-runner v", output);
        Assert.False(File.Exists(_logPath));
        Assert.False(Directory.Exists(Path.GetDirectoryName(_logPath)!));
    }

    /// <summary>
    /// With the variable set, even a run that compiles nothing records the process
    /// floor: one process row, no bundle rows, a real wall clock and a real RSS.
    /// That row is the baseline the wall-clock-minus-phases residual is read against.
    /// </summary>
    [Fact]
    public void WithTheEnvVar_AZeroBundleRunStillRecordsTheProcessFloor()
    {
        var (output, exit) = RunRaw(_logPath, "--version");
        Assert.Equal(0, exit);
        Assert.Contains("al-runner v", output);

        Assert.Empty(ReadRecords(_logPath, "bundle"));
        var proc = Assert.Single(ReadRecords(_logPath, "process"));
        Assert.Equal(0, proc.GetProperty("bundles_in_process").GetInt32());
        Assert.Equal(0, proc.GetProperty("patches_ms").GetInt64());
        Assert.True(proc.GetProperty("wall_ms").GetInt64() > 0);
        Assert.True(proc.GetProperty("peak_rss_bytes").GetInt64() > 8L * 1024 * 1024);
    }
}
