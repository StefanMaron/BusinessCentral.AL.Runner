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
                "application": "1.0.0.0",
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
        // `[r2r] re-execing` is printed BEFORE Log.Install() and survives, but
        // `[Cecil] Fresh rewrite done` is printed after it and does not. Without this,
        // the marker-derived expected-parent count silently under-counts on a cold
        // Cecil cache and the test is a coin flip on cache state.
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

        var (output, exit) = RunBundles(_logPath, _noDeps, _withDeps);
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
        // because the CI runner-extras step packs 38 app groups into ONE bundle and this
        // is the only level at which those 38 are separately visible.
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

        // One "runner spawn" is not one OS process. The runner re-execs itself with
        // DOTNET_ReadyToRun=0 on every invocation that does not already have it set
        // (the [r2r] branch), and again after a fresh Cecil rewrite of Ncl.dll — so a
        // cold spawn is up to THREE processes, each waiting on the next. Every outer
        // process's wall clock contains its child's, so summing them all under
        // kind=="process" would multiply every total the aggregate reports. They are
        // kept, under a distinct kind, because outer − inner is the real cost of an
        // extra process start — a per-spawn tax worth sizing, not worth hiding.
        //
        // Asserted against the re-exec markers the runner prints rather than a fixed
        // count: EVERY re-exec path must be labelled, and this fails if a new one is
        // added without labelling it, or an existing label is dropped.
        var expectedParents =
            CountOccurrences(output, "[r2r] re-execing")
            + CountOccurrences(output, "[Cecil] Fresh rewrite done");
        var parents = ReadRecords(_logPath, "process-reexec-parent");
        // Not vacuous: the DOTNET_ReadyToRun=0 re-exec fires on every invocation that
        // does not already have it preset, so there is always at least one.
        Assert.True(expectedParents >= 1,
            $"no re-exec marker in the runner output — the assertion below would be vacuous:\n{output}");
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
