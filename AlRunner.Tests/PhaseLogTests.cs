// PhaseLogTests — pins the record format and the append semantics of the
// AL_RUNNER_PHASE_LOG diagnostic (issue #1825).
//
// The instrument exists to answer "where do the matrix's minutes actually go":
// per-BUNDLE records for the compile/run work, one per-PROCESS record for the
// once-per-process cost (engine boot, host startup + full-opt JIT, peak RSS).
// Two properties must hold or the aggregate built on top of it is garbage:
//
//   1. Every field the aggregate reads must be present, correctly named, and
//      carry the value the runner measured — not a default.
//   2. Concurrent writers must not corrupt each other. Since #1818 the unit-test
//      suite runs 4-way parallel, so up to four runner processes append to the
//      same file at once; this is now the only non-atomic write of its kind left
//      in the tree. A destructive interleave silently loses spawns and skews
//      every percentile computed from the file.
using System.Collections.Concurrent;
using System.Text.Json;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class PhaseLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "al-runner-phaselog-" + Guid.NewGuid().ToString("N"));

    public PhaseLogTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static PhaseLogRecord SampleBundle() => new()
    {
        Kind = "bundle",
        Pid = 4242,
        Bundle = "tests/runner-extras/crypto hash instream",
        BundleIndex = 7,
        BundlesInProcess = 38,
        DepsResolved = 5,
        DepAssembliesLoaded = 3,
        EmitMs = 16200,
        CompileMs = 5800,
        RunMs = 12500,
        CacheHits = 2,
        CacheMisses = 1,
        WallMs = 34900,
    };

    private static PhaseLogRecord SampleApp() => new()
    {
        Kind = "app",
        Pid = 4242,
        Bundle = "tests/runner-extras",
        BundleIndex = 1,
        BundlesInProcess = 1,
        App = "V2_crypto-hash-instream",
        AppIndex = 3,
        AppsInBundle = 38,
        EmitMs = 5119,
        CompileMs = 548,
        RunMs = 3222,
        CacheHits = 0,
        CacheMisses = 1,
        WallMs = 11573,
    };

    private static PhaseLogRecord SampleProcess() => new()
    {
        Kind = "process",
        Pid = 4242,
        Bundle = "crypto-hash-instream",
        BundlesInProcess = 38,
        PackageCacheDirs = 1,
        DepsResolved = 51,
        DepAssembliesLoaded = 33,
        PatchesMs = 4517,
        EmitMs = 1100,
        CompileMs = 4000,
        RunMs = 400,
        CacheHits = 0,
        CacheMisses = 1,
        WallMs = 12000,
        PeakRssBytes = 1_234_567_890,
        ExitCode = 3,
    };

    /// <summary>
    /// A bundle record round-trips every field the CI aggregate reads, with the
    /// exact key names it reads them under. Concrete non-default values throughout:
    /// a formatter that dropped a field or emitted a zero would fail here.
    /// </summary>
    [Fact]
    public void BundleRecord_SerialisesEveryFieldTheAggregateReads()
    {
        var json = JsonDocument.Parse(SampleBundle().ToJsonLine()).RootElement;

        Assert.Equal("bundle", json.GetProperty("kind").GetString());
        Assert.Equal(4242, json.GetProperty("pid").GetInt32());
        Assert.Equal("tests/runner-extras/crypto hash instream", json.GetProperty("bundle").GetString());
        Assert.Equal(7, json.GetProperty("bundle_index").GetInt32());
        Assert.Equal(38, json.GetProperty("bundles_in_process").GetInt32());
        Assert.Equal(5, json.GetProperty("deps_resolved").GetInt32());
        Assert.Equal(3, json.GetProperty("dep_assemblies_loaded").GetInt32());
        Assert.Equal(16200, json.GetProperty("emit_ms").GetInt64());
        Assert.Equal(5800, json.GetProperty("compile_ms").GetInt64());
        Assert.Equal(12500, json.GetProperty("run_ms").GetInt64());
        Assert.Equal(2, json.GetProperty("cache_hits").GetInt32());
        Assert.Equal(1, json.GetProperty("cache_misses").GetInt32());
        Assert.Equal(34900, json.GetProperty("wall_ms").GetInt64());
    }

    /// <summary>
    /// The once-per-process costs must NOT appear on bundle rows. Duplicating a
    /// 4.5 s engine boot onto all bundle records of a runner-extras run (23 app
    /// groups as of #1847's consolidation, was 38 before it) would invent tens of
    /// seconds of cost that was never paid, which is precisely the kind of
    /// arithmetic this instrument exists to stop.
    /// </summary>
    [Fact]
    public void BundleRecord_OmitsTheOncePerProcessFields()
    {
        var json = JsonDocument.Parse(SampleBundle().ToJsonLine()).RootElement;

        Assert.False(json.TryGetProperty("patches_ms", out _));
        Assert.False(json.TryGetProperty("peak_rss_bytes", out _));
        Assert.False(json.TryGetProperty("package_cache_dirs", out _));
        Assert.False(json.TryGetProperty("exit_code", out _));
    }

    /// <summary>
    /// The #1828 breakdown: the bundle row carries named stages for the work it does
    /// outside every app group, in the order they were entered, with repeated entries
    /// of the same name summed rather than overwritten (dep-load enters once per
    /// dependency; the app loop's stages are entered once per app group).
    ///
    /// Order is asserted, not just membership: the whole point of the breakdown is to
    /// read the bundle's turn in execution order, and a Dictionary does not promise it.
    /// </summary>
    [Fact]
    public void BundleRecord_SerialisesItsStageBreakdownInOrderWithRepeatsSummed()
    {
        var row = SampleBundle();
        row.Stages.Add(new KeyValuePair<string, long>("dep-resolve", 512));
        row.Stages.Add(new KeyValuePair<string, long>("dep-load:Library Assert", 22075));
        row.Stages.Add(new KeyValuePair<string, long>("sibling-symbols", 29340));

        var json = JsonDocument.Parse(row.ToJsonLine()).RootElement;
        var stages = json.GetProperty("stages");

        Assert.Equal(
            new[] { "dep-resolve", "dep-load:Library Assert", "sibling-symbols" },
            stages.EnumerateObject().Select(p => p.Name));
        Assert.Equal(512, stages.GetProperty("dep-resolve").GetInt64());
        Assert.Equal(22075, stages.GetProperty("dep-load:Library Assert").GetInt64());
        Assert.Equal(29340, stages.GetProperty("sibling-symbols").GetInt64());
        // Still exactly one line — the breakdown must not break the JSONL contract.
        Assert.Equal(1, row.ToJsonLine().Count(c => c == '\n'));
    }

    /// <summary>
    /// A row with nothing measured omits the "stages" key entirely rather than
    /// emitting an empty object — true for bundle AND app rows (both can legitimately
    /// carry a breakdown; see <see cref="AppRecord_SerialisesItsStageBreakdownInOrderWithRepeatsSummed"/>
    /// for #1861's app-level sibling of #1828's bundle-level breakdown).
    /// </summary>
    [Fact]
    public void StagesOmittedWhenNoneWereMeasured()
    {
        Assert.False(JsonDocument.Parse(SampleApp().ToJsonLine()).RootElement.TryGetProperty("stages", out _));
        Assert.False(JsonDocument.Parse(SampleBundle().ToJsonLine()).RootElement.TryGetProperty("stages", out _));
    }

    /// <summary>
    /// Stages must NEVER appear on a process row: the once-per-process costs already
    /// have their own named fields (PatchesMs, PeakRssBytes, …), and a "stages" object
    /// there would be a second, redundant way to report the same numbers — the kind of
    /// duplication that makes an aggregate double-count if it ever sums both.
    /// </summary>
    [Fact]
    public void StagesNeverAppearOnProcessRows()
    {
        var proc = SampleProcess();
        proc.Stages.Add(new KeyValuePair<string, long>("dep-resolve", 512));

        Assert.False(JsonDocument.Parse(proc.ToJsonLine()).RootElement.TryGetProperty("stages", out _));
    }

    /// <summary>
    /// The #1861 breakdown: an app row carries named stages for the work done inside
    /// its own run turn that is NOT one of the reported test durations — the flat
    /// ~4.8s-per-app-group tax #1861 measured (110.5s of 128.8s "test run" phase,
    /// essentially constant across 23 wildly different app groups). Same ordering and
    /// repeat-summing contract as the bundle-level breakdown (#1828).
    /// </summary>
    [Fact]
    public void AppRecord_SerialisesItsStageBreakdownInOrderWithRepeatsSummed()
    {
        var row = SampleApp();
        row.Stages.Add(new KeyValuePair<string, long>("set-test-assembly", 120));
        row.Stages.Add(new KeyValuePair<string, long>("type-discovery", 340));
        row.Stages.Add(new KeyValuePair<string, long>("install-seed", 4100));

        var json = JsonDocument.Parse(row.ToJsonLine()).RootElement;
        var stages = json.GetProperty("stages");

        Assert.Equal(
            new[] { "set-test-assembly", "type-discovery", "install-seed" },
            stages.EnumerateObject().Select(p => p.Name));
        Assert.Equal(120, stages.GetProperty("set-test-assembly").GetInt64());
        Assert.Equal(340, stages.GetProperty("type-discovery").GetInt64());
        Assert.Equal(4100, stages.GetProperty("install-seed").GetInt64());
        // Still exactly one line — the breakdown must not break the JSONL contract.
        Assert.Equal(1, row.ToJsonLine().Count(c => c == '\n'));
    }

    /// <summary>
    /// The process record carries them, with the measured values.
    /// </summary>
    [Fact]
    public void ProcessRecord_CarriesTheOncePerProcessFields()
    {
        var json = JsonDocument.Parse(SampleProcess().ToJsonLine()).RootElement;

        Assert.Equal("process", json.GetProperty("kind").GetString());
        Assert.Equal(1, json.GetProperty("package_cache_dirs").GetInt32());
        Assert.Equal(4517, json.GetProperty("patches_ms").GetInt64());
        Assert.Equal(1_234_567_890, json.GetProperty("peak_rss_bytes").GetInt64());
        Assert.Equal(3, json.GetProperty("exit_code").GetInt32());
        Assert.Equal(12000, json.GetProperty("wall_ms").GetInt64());
        Assert.Equal(38, json.GetProperty("bundles_in_process").GetInt32());
        // No bundle_index on a process row — it does not belong to one bundle.
        Assert.False(json.TryGetProperty("bundle_index", out _));
    }

    /// <summary>
    /// The app row is the finest unit — one emitted module, which is where emit,
    /// compile and run actually happen. It must locate itself both within its bundle
    /// AND within the process, because CI passes `tests/runner-extras` as ONE bundle
    /// containing many app groups (23 as of #1847's consolidation): without app rows
    /// that whole step reports a single data point and the per-unit tax it is meant
    /// to expose stays invisible.
    /// </summary>
    [Fact]
    public void AppRecord_LocatesItselfWithinItsBundleAndProcess()
    {
        var json = JsonDocument.Parse(SampleApp().ToJsonLine()).RootElement;

        Assert.Equal("app", json.GetProperty("kind").GetString());
        Assert.Equal("tests/runner-extras", json.GetProperty("bundle").GetString());
        Assert.Equal(1, json.GetProperty("bundle_index").GetInt32());
        Assert.Equal("V2_crypto-hash-instream", json.GetProperty("app").GetString());
        Assert.Equal(3, json.GetProperty("app_index").GetInt32());
        Assert.Equal(38, json.GetProperty("apps_in_bundle").GetInt32());
        Assert.Equal(5119, json.GetProperty("emit_ms").GetInt64());
        Assert.Equal(548, json.GetProperty("compile_ms").GetInt64());
        Assert.Equal(3222, json.GetProperty("run_ms").GetInt64());
        Assert.Equal(1, json.GetProperty("cache_misses").GetInt32());
        Assert.Equal(11573, json.GetProperty("wall_ms").GetInt64());
        // Still not a process row.
        Assert.False(json.TryGetProperty("patches_ms", out _));
        Assert.False(json.TryGetProperty("peak_rss_bytes", out _));
    }

    /// <summary>
    /// The app-only fields must not leak onto bundle or process rows, where they
    /// would be meaningless (a bundle is not "app 3 of 38").
    /// </summary>
    [Fact]
    public void BundleAndProcessRecords_OmitTheAppOnlyFields()
    {
        foreach (var line in new[] { SampleBundle().ToJsonLine(), SampleProcess().ToJsonLine() })
        {
            var json = JsonDocument.Parse(line).RootElement;
            Assert.False(json.TryGetProperty("app", out _));
            Assert.False(json.TryGetProperty("app_index", out _));
            Assert.False(json.TryGetProperty("apps_in_bundle", out _));
        }
    }

    /// <summary>
    /// Every row carries start_ms, the Unix-epoch millisecond at which its clock started
    /// (issue #1829). Durations alone answer "how much work" but not "when were the workers
    /// idle" — start_ms + wall_ms is an interval, and a set of intervals is an occupancy
    /// timeline. Without it a 1.83x concurrency figure cannot be told apart from a
    /// saturated run with a long single-threaded tail, which is what it turned out to be.
    /// Asserted on all three kinds because the aggregate reads them uniformly.
    /// </summary>
    [Fact]
    public void EveryRecordKind_CarriesTheStartTimestampTheTimelineIsBuiltFrom()
    {
        foreach (var (kind, record) in new (string, PhaseLogRecord)[]
                 {
                     ("bundle", SampleBundle()), ("app", SampleApp()), ("process", SampleProcess()),
                 })
        {
            record.StartMs = 1_760_000_000_123;
            var json = JsonDocument.Parse(record.ToJsonLine()).RootElement;
            Assert.Equal(kind, json.GetProperty("kind").GetString());
            Assert.Equal(1_760_000_000_123, json.GetProperty("start_ms").GetInt64());
        }
    }

    /// <summary>A record is exactly one physical line — JSONL, not pretty-printed.</summary>
    [Fact]
    public void ToJsonLine_IsExactlyOneLine()
    {
        var line = SampleBundle().ToJsonLine();
        Assert.EndsWith("\n", line);
        Assert.Single(line.TrimEnd('\n').Split('\n'));
    }

    /// <summary>
    /// Appending accumulates rather than truncating, and each append lands as one
    /// whole line — the read-modify-write shape this deliberately is not.
    /// </summary>
    [Fact]
    public void Append_AccumulatesRecordsInsteadOfTruncating()
    {
        var path = Path.Combine(_dir, "phases.jsonl");
        PhaseLog.Append(path, SampleBundle().ToJsonLine());
        PhaseLog.Append(path, SampleProcess().ToJsonLine());

        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
        Assert.Equal("bundle", JsonDocument.Parse(lines[0]).RootElement.GetProperty("kind").GetString());
        Assert.Equal("process", JsonDocument.Parse(lines[1]).RootElement.GetProperty("kind").GetString());
    }

    /// <summary>Append creates the file (and its directory) on first write.</summary>
    [Fact]
    public void Append_CreatesTheFileAndItsDirectory()
    {
        var path = Path.Combine(_dir, "nested", "deeper", "phases.jsonl");
        PhaseLog.Append(path, SampleProcess().ToJsonLine());
        Assert.True(File.Exists(path));
        Assert.Single(File.ReadAllLines(path));
    }

    /// <summary>
    /// The property that actually matters under #1818's 4-way parallel suite:
    /// N concurrent writers produce N intact, individually parseable lines with
    /// no losses and no torn/interleaved content.
    ///
    /// 8 writers × 60 records is deliberately more contention than CI's 4, so a
    /// non-atomic implementation (buffered stream, seek-then-write, read-modify-write)
    /// fails here reliably rather than one run in twenty.
    /// </summary>
    [Fact]
    public void Append_UnderConcurrentWriters_LosesNoRecordsAndTearsNoLine()
    {
        const int writers = 8, perWriter = 60;
        var path = Path.Combine(_dir, "concurrent.jsonl");

        Parallel.For(0, writers, w =>
        {
            for (var i = 0; i < perWriter; i++)
            {
                var rec = SampleBundle();
                rec.Pid = w;
                rec.BundleIndex = i;
                rec.Bundle = $"writer-{w}-record-{i}";
                PhaseLog.Append(path, rec.ToJsonLine());
            }
        });

        var lines = File.ReadAllLines(path);
        Assert.Equal(writers * perWriter, lines.Length);

        var seen = new ConcurrentDictionary<string, byte>();
        foreach (var line in lines)
        {
            // Parse failure here == a torn line: two writers' bytes in one record.
            var el = JsonDocument.Parse(line).RootElement;
            Assert.True(seen.TryAdd(el.GetProperty("bundle").GetString()!, 0));
        }

        for (var w = 0; w < writers; w++)
            for (var i = 0; i < perWriter; i++)
                Assert.Contains($"writer-{w}-record-{i}", seen.Keys);
    }

    /// <summary>
    /// Negative direction for the writer itself: an unwritable path must not take
    /// the run down. The phase log is a diagnostic bolted onto a test runner — a
    /// bad AL_RUNNER_PHASE_LOG value may cost the measurement, never the run.
    /// It still reports the failure on stderr rather than swallowing it.
    /// </summary>
    [Fact]
    public void Append_ToAnUnwritablePath_ReportsOnStderrAndDoesNotThrow()
    {
        // A path whose "directory" is an existing regular file cannot be created.
        var blocker = Path.Combine(_dir, "blocker");
        File.WriteAllText(blocker, "not a directory");
        var path = Path.Combine(blocker, "phases.jsonl");

        var saved = Console.Error;
        var captured = new StringWriter();
        try
        {
            Console.SetError(captured);
            PhaseLog.Append(path, SampleProcess().ToJsonLine());
        }
        finally
        {
            Console.SetError(saved);
        }

        Assert.Contains("AL_RUNNER_PHASE_LOG", captured.ToString());
        Assert.False(File.Exists(path));
    }

    /// <summary>
    /// Peak RSS must be a real measurement, not a placeholder. Any live .NET
    /// process has a high-water mark of at least a few MB; a stubbed 0 (or a
    /// value below what this very test process has already touched) is a lie the
    /// aggregate would report as "the runner used no memory".
    /// </summary>
    [Fact]
    public void PeakRssBytes_ReportsARealHighWaterMark()
    {
        var rss = PhaseLog.PeakRssBytes();
        Assert.True(rss > 8 * 1024 * 1024, $"peak RSS looks stubbed: {rss} bytes");
    }
}
