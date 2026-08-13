// PhaseLog — opt-in, append-only, per-bundle + per-process cost instrumentation.
//
// Why (issue #1825)
// -----------------
// "Where does the CI matrix's wall clock actually go" is a question this repo has
// now asked three times, and each time it was answered by extrapolating from one
// hand-read log sample. The two facts that need measuring are:
//
//   * a per-PROCESS fixed tax — engine boot (`BC runtime patches applied`, ~4.5 s),
//     host startup and full-opt JIT (AlRunner.csproj sets
//     <TieredCompilation>false</TieredCompilation> so JmpHooks written at tier-0
//     addresses survive, which every process pays for at startup), and
//   * a per-BUNDLE tax, which is the one that dominates the single-process steps:
//     `Run runner-extras` originally compiled 38 bundles and took 2.5x as long as
//     `Run al-language corpus`, which has 5x the AL source in ONE bundle. Boot is
//     paid once in each, so it cannot be the explanation. (#1847 folded 16 of
//     those 38 bundles together, cutting the count to 23 — the per-bundle tax
//     this instrument measures is exactly what motivated that consolidation.)
//
// One record per process cannot see the second of those, so the primary unit here
// is the BUNDLE record, with a single process record carrying the once-per-process
// figures. `bundle_index` / `bundles_in_process` make the ordering recoverable, so
// "the 38th bundle costs more than the 2nd" (a quadratic term) is distinguishable
// from a flat per-bundle tax — a completely different bug with a different fix.
//
// Why a file and not stdout
// -------------------------
// AlRunner.Tests captures each spawned runner's stdout into a string and discards
// it unless the test fails, and several of those tests assert on the runner's exact
// stdout. Extra stdout lines would be both invisible and breaking. A file sidesteps
// both.
//
// Concurrency contract
// --------------------
// Since #1818 the unit-test suite runs 4-way parallel, so up to four runner
// processes append to the same log at once. Every record is written whole, under an
// exclusive open, never as a read-modify-write and never through a shared buffered
// writer. `FileMode.Append` alone is NOT sufficient — see the measurement in
// Append below, where the obvious FileShare.ReadWrite version silently loses 30% of
// its records. This is the last non-atomic write of its kind in the tree after
// #1818 made the dependency-source cache and the symbol cache atomic; do not
// "simplify" it back.
//
// Cost when unset
// ---------------
// AL_RUNNER_PHASE_LOG is read once into a static. Unset, `Install` registers no
// exit hook and every Note*/Begin*/End* entry point returns on a null check before
// allocating or reading a clock. Production pays a predictable-branch per call site.
using System.Text;
using System.Text.Json;

namespace AlRunner.Infrastructure;

/// <summary>One line of the phase log. See <see cref="PhaseLog"/> for the contract.</summary>
public sealed class PhaseLogRecord
{
    /// <summary>"app", "bundle", "process", or "process-reexec-parent" (see <see cref="PhaseLog.MarkReexecParent"/>).</summary>
    public string Kind { get; set; } = "bundle";
    public int Pid { get; set; }
    /// <summary>Bundle path for a bundle/app row; the first bundle argument (or "") for a process row.</summary>
    public string Bundle { get; set; } = "";
    /// <summary>1-based position of this bundle in the process's bundle list. Bundle and app rows.</summary>
    public int BundleIndex { get; set; }
    public int BundlesInProcess { get; set; }

    // ── App-row only. The app group (one emitted module) is the finest unit and the
    // one that actually pays emit + compile + run; a bundle may hold dozens of them.
    /// <summary>Emitted module name. App rows only.</summary>
    public string App { get; set; } = "";
    /// <summary>1-based position of this app within its bundle. App rows only.</summary>
    public int AppIndex { get; set; }
    public int AppsInBundle { get; set; }
    public int DepsResolved { get; set; }
    public int DepAssembliesLoaded { get; set; }
    public long EmitMs { get; set; }
    public long CompileMs { get; set; }
    public long RunMs { get; set; }
    public int CacheHits { get; set; }
    public int CacheMisses { get; set; }
    /// <summary>Bundle rows: wall clock of the bundle's whole turn in the loop. Process rows: since OS process start.</summary>
    public long WallMs { get; set; }

    /// <summary>
    /// Unix-epoch millisecond at which this row's clock started, so (StartMs, StartMs +
    /// WallMs) is an interval and a set of rows is an occupancy TIMELINE rather than a bag
    /// of durations (issue #1829). Summed wall clock over a step says how much work
    /// happened; only the timeline says when the workers were idle — the difference between
    /// "the thread cap is too low" and "the longest unit was scheduled last", which have
    /// nothing in common as fixes. Epoch rather than a process-relative clock because rows
    /// from up to four concurrent processes have to land on one axis.
    /// </summary>
    public long StartMs { get; set; }

    // ── Once-per-process fields. Deliberately ABSENT from bundle rows: duplicating a
    // 4.5 s engine boot onto all 38 rows of a runner-extras run would invent ~170 s
    // of cost that was never paid.
    public int PackageCacheDirs { get; set; }
    public long PatchesMs { get; set; }
    public long PeakRssBytes { get; set; }
    public int ExitCode { get; set; }

    // ── Bundle-row only. Named slices of the bundle's turn that are NOT inside any
    // app group — the block #1828 exists to attribute. Insertion-ordered (a plain
    // Dictionary does not promise that) so the JSON reads in execution order, which
    // is how "this stage happens once, that one per app" is spotted by eye.
    /// <summary>Bundle-level stage timings, in the order the stages were first entered.</summary>
    public List<KeyValuePair<string, long>> Stages { get; } = new();

    private bool IsAppRow => Kind == "app";
    private bool IsProcessRow => Kind != "bundle" && Kind != "app";

    /// <summary>Serialises to exactly one JSONL line, newline included.</summary>
    public string ToJsonLine()
    {
        var sb = new StringBuilder(320);
        sb.Append('{');
        Str(sb, "kind", Kind);
        Num(sb, "pid", Pid);
        Str(sb, "bundle", Bundle);
        if (!IsProcessRow) Num(sb, "bundle_index", BundleIndex);
        Num(sb, "bundles_in_process", BundlesInProcess);
        if (IsAppRow)
        {
            Str(sb, "app", App);
            Num(sb, "app_index", AppIndex);
            Num(sb, "apps_in_bundle", AppsInBundle);
        }
        Num(sb, "deps_resolved", DepsResolved);
        Num(sb, "dep_assemblies_loaded", DepAssembliesLoaded);
        Num(sb, "emit_ms", EmitMs);
        Num(sb, "compile_ms", CompileMs);
        Num(sb, "run_ms", RunMs);
        Num(sb, "cache_hits", CacheHits);
        Num(sb, "cache_misses", CacheMisses);
        Num(sb, "wall_ms", WallMs);
        Num(sb, "start_ms", StartMs);
        if (IsProcessRow)
        {
            Num(sb, "package_cache_dirs", PackageCacheDirs);
            Num(sb, "patches_ms", PatchesMs);
            Num(sb, "peak_rss_bytes", PeakRssBytes);
            Num(sb, "exit_code", ExitCode);
        }
        // Bundle rows only, and only when something was measured: an app row must not
        // carry a bundle-level stage (it did not pay it), and an empty object on every
        // bundle row would be noise.
        if (Kind == "bundle" && Stages.Count > 0)
        {
            Sep(sb);
            sb.Append("\"stages\":{");
            for (var i = 0; i < Stages.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(JsonSerializer.Serialize(Stages[i].Key)).Append(':')
                  .Append(Stages[i].Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            sb.Append('}');
        }
        sb.Append("}\n");
        return sb.ToString();

        static void Sep(StringBuilder b) { if (b.Length > 1) b.Append(','); }
        static void Str(StringBuilder b, string k, string v)
        {
            Sep(b);
            b.Append('"').Append(k).Append("\":").Append(JsonSerializer.Serialize(v));
        }
        static void Num(StringBuilder b, string k, long v)
        {
            Sep(b);
            b.Append('"').Append(k).Append("\":").Append(v.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}

public static class PhaseLog
{
    private static readonly string? Path_ =
        Environment.GetEnvironmentVariable("AL_RUNNER_PHASE_LOG") is { Length: > 0 } p ? p : null;

    /// <summary>True when AL_RUNNER_PHASE_LOG names a path. Everything below is inert otherwise.</summary>
    public static bool Enabled => Path_ != null;

    private static readonly object Gate = new();
    private static readonly PhaseLogRecord Process_ = new() { Kind = "process" };

    private static System.Diagnostics.Stopwatch? _bundleClock;
    private static PhaseLogRecord? _bundle;
    private static System.Diagnostics.Stopwatch? _appClock;
    private static PhaseLogRecord? _app;
    /// <summary>Open app rows of the current bundle, in first-seen order. Flushed by EndBundle.</summary>
    private static readonly List<PhaseLogRecord> Apps = new();
    private static readonly Dictionary<string, PhaseLogRecord> AppsByName = new(StringComparer.Ordinal);
    private static bool _installed;

    /// <summary>
    /// Registers the process-record writer. Called as early as possible in Main so
    /// even `--help` / `--version` — which return before any BC type loads — record
    /// the bare process floor (startup + full-opt JIT, zero phases). Those rows are
    /// the baseline the wall-clock-minus-phases residual is read against.
    /// </summary>
    public static void Install()
    {
        if (!Enabled || _installed) return;
        _installed = true;
        Process_.Pid = Environment.ProcessId;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => WriteProcessRecord();
    }

    /// <summary>
    /// Re-labels this process's row so aggregates can exclude it. The runner re-execs
    /// itself on two paths — DOTNET_ReadyToRun=0 (every invocation) and a fresh Cecil
    /// rewrite of Ncl.dll — and each time the outer process blocks on the child, so its
    /// wall clock CONTAINS the child's entire run. Summing them under one kind would
    /// multiply every total. Keeping the row (rather than dropping it) preserves the
    /// re-exec's own cost, which is a real per-spawn tax.
    /// </summary>
    public static void MarkReexecParent()
    {
        if (!Enabled) return;
        Process_.Kind = "process-reexec-parent";
    }

    public static void SetPackageCacheDirs(int count)
    {
        if (!Enabled) return;
        Process_.PackageCacheDirs = count;
    }

    public static void SetPatchesMs(long ms)
    {
        if (!Enabled) return;
        Process_.PatchesMs = ms;
    }

    /// <summary>Records the bundle list up front so a row can be located in it.</summary>
    public static void SetBundles(IReadOnlyList<string> bundles)
    {
        if (!Enabled) return;
        Process_.BundlesInProcess = bundles.Count;
        if (bundles.Count > 0)
            Process_.Bundle = System.IO.Path.GetFileName(
                System.IO.Path.GetFullPath(bundles[0]).TrimEnd(
                    System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
    }

    /// <summary>Opens a bundle row. Ended (and written) by <see cref="EndBundle"/>.</summary>
    public static void BeginBundle(string bundle, int index)
    {
        if (!Enabled) return;
        // The bundle loop can `continue` before reaching EndBundle (e.g. a bundle with
        // no suites), which would otherwise drop that bundle's row and silently orphan
        // any app rows it had opened. Flushing here writes it with the zero phases it
        // genuinely accrued instead of losing the spawn from the distribution.
        EndBundle(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);
        lock (Gate)
        {
            _bundle = new PhaseLogRecord
            {
                Kind = "bundle",
                Pid = Environment.ProcessId,
                Bundle = bundle,
                BundleIndex = index,
                BundlesInProcess = Process_.BundlesInProcess,
                StartMs = NowMs(),
            };
            _bundleClock = System.Diagnostics.Stopwatch.StartNew();
            Apps.Clear();
            AppsByName.Clear();
        }
    }

    /// <summary>
    /// Opens (or REOPENS) an app-group row — one emitted module, the unit that actually
    /// pays emit, compile and run. Auto-closes any previously open one, so the many
    /// `continue` paths through the app loops cannot leak a row.
    ///
    /// Reopening by name is load-bearing, not convenience. Bundled mode runs the app
    /// groups in TWO passes — emit/compile/load for every app, then a second walk that
    /// executes each loaded assembly's tests — because every app's types must be in the
    /// AppDomain before any test runs. A row therefore accumulates across both passes,
    /// and its wall clock is the sum of its turns. Attributing the second pass to
    /// whichever row happened to be open instead put the entire 112 s test run of a
    /// 38-app bundle onto app #38.
    ///
    /// Dependency counts are inherited from the enclosing bundle: resolution happens
    /// once per bundle, before these loops, so an app row reports the closure it was
    /// compiled against rather than a misleading zero.
    /// </summary>
    public static void BeginApp(string app, int index, int appsInBundle)
    {
        if (!Enabled) return;
        EndApp();
        lock (Gate)
        {
            if (!AppsByName.TryGetValue(app, out var row))
            {
                row = new PhaseLogRecord
                {
                    Kind = "app",
                    Pid = Environment.ProcessId,
                    Bundle = _bundle?.Bundle ?? "",
                    BundleIndex = _bundle?.BundleIndex ?? 0,
                    BundlesInProcess = Process_.BundlesInProcess,
                    App = app,
                    AppIndex = index,
                    AppsInBundle = appsInBundle,
                    DepsResolved = _bundle?.DepsResolved ?? 0,
                    DepAssembliesLoaded = _bundle?.DepAssembliesLoaded ?? 0,
                    // First-seen, deliberately: an app row is REOPENED by the second
                    // (test-execution) pass, and the interval that matters for a timeline is
                    // "when did this module first start costing anything", not the later turn.
                    StartMs = NowMs(),
                };
                AppsByName[app] = row;
                Apps.Add(row);
            }
            _app = row;
            _appClock = System.Diagnostics.Stopwatch.StartNew();
        }
    }

    /// <summary>
    /// Closes the open app row, banking its elapsed time. Idempotent. The row is not
    /// appended yet — it may be reopened by the second pass; <see cref="EndBundle"/>
    /// writes them all out.
    /// </summary>
    public static void EndApp()
    {
        if (!Enabled) return;
        lock (Gate)
        {
            if (_app == null) return;
            _app.WallMs += _appClock?.ElapsedMilliseconds ?? 0;
            _app = null;
            _appClock = null;
        }
    }

    public static void AddAppEmit(TimeSpan t) => AddApp(r => r.EmitMs += (long)t.TotalMilliseconds);

    public static void AddAppCompile(TimeSpan t) => AddApp(r => r.CompileMs += (long)t.TotalMilliseconds);

    public static void AddAppRun(TimeSpan t) => AddApp(r => r.RunMs += (long)t.TotalMilliseconds);

    private static void AddApp(Action<PhaseLogRecord> f)
    {
        if (!Enabled) return;
        lock (Gate) { if (_app != null) f(_app); }
    }

    /// <summary>
    /// Times one named slice of the bundle's turn that is NOT inside any app group,
    /// and adds it to the open bundle row.
    ///
    /// This is the #1828 instrument. #1826 measured `bundle wall − Σ app wall` at
    /// 152.3 s on a 357.8 s runner-extras leg (43%) and could say nothing about what
    /// it was, because the bundle turn was one opaque span either side of the app
    /// loops. Stages cut that span up.
    ///
    /// Two rules make the arithmetic trustworthy, and both are checked by
    /// PhaseLogTests:
    ///   * stages must not NEST — a nested stage is counted twice and the sum then
    ///     exceeds the wall clock it is supposed to decompose;
    ///   * stages must not overlap an app group — app time is already reported per
    ///     app, and double-counting it here would manufacture overhead that the
    ///     report would then chase.
    /// Whatever the named stages do not cover stays visible as the report's
    /// "unattributed" line rather than being silently absorbed.
    ///
    /// Inert when unset: the returned struct holds a null name, allocates nothing and
    /// reads no clock.
    /// </summary>
    public static StageScope Stage(string name) => new(Enabled ? name : null);

    /// <summary>Adds a stage duration directly, for call sites that cannot use `using`.</summary>
    public static void AddStage(string name, TimeSpan elapsed)
    {
        if (!Enabled) return;
        var ms = (long)elapsed.TotalMilliseconds;
        lock (Gate)
        {
            if (_bundle == null) return;
            var stages = _bundle.Stages;
            for (var i = 0; i < stages.Count; i++)
            {
                if (!string.Equals(stages[i].Key, name, StringComparison.Ordinal)) continue;
                stages[i] = new KeyValuePair<string, long>(name, stages[i].Value + ms);
                return;
            }
            stages.Add(new KeyValuePair<string, long>(name, ms));
        }
    }

    /// <summary>Scope returned by <see cref="Stage"/>. A struct, so the disabled path allocates nothing.</summary>
    public readonly struct StageScope : IDisposable
    {
        private readonly string? _name;
        private readonly long _start;

        internal StageScope(string? name)
        {
            _name = name;
            _start = name == null ? 0 : System.Diagnostics.Stopwatch.GetTimestamp();
        }

        public void Dispose()
        {
            if (_name != null) AddStage(_name, System.Diagnostics.Stopwatch.GetElapsedTime(_start));
        }
    }

    public static void NoteDepsResolved(int count) => Bump(r => r.DepsResolved += count, p => p.DepsResolved += count);

    public static void NoteDepAssembliesLoaded(int count) =>
        Bump(r => r.DepAssembliesLoaded += count, p => p.DepAssembliesLoaded += count);

    public static void NoteCacheHit() => Bump(r => r.CacheHits++, p => p.CacheHits++);

    public static void NoteCacheMiss() => Bump(r => r.CacheMisses++, p => p.CacheMisses++);

    private static void Bump(Action<PhaseLogRecord> onBundle, Action<PhaseLogRecord> onProcess)
    {
        if (!Enabled) return;
        lock (Gate)
        {
            // Cache decisions are made per app group, so they land on the open app row
            // too — that is how "which apps MISSed" is recoverable from the log.
            if (_app != null) onBundle(_app);
            if (_bundle != null) onBundle(_bundle);
            onProcess(Process_);
        }
    }

    /// <summary>
    /// Closes the open bundle row with its measured phase times and appends it.
    /// Written per bundle rather than buffered to process exit so a run that dies
    /// mid-way still yields every bundle it did finish.
    /// </summary>
    public static void EndBundle(TimeSpan emit, TimeSpan compile, TimeSpan run)
    {
        if (!Enabled) return;
        EndApp(); // a bundle cannot end with one of its apps still open
        PhaseLogRecord row;
        List<PhaseLogRecord> apps;
        lock (Gate)
        {
            if (_bundle == null) return;
            apps = new List<PhaseLogRecord>(Apps);
            Apps.Clear();
            AppsByName.Clear();
            row = _bundle;
            row.EmitMs = (long)emit.TotalMilliseconds;
            row.CompileMs = (long)compile.TotalMilliseconds;
            row.RunMs = (long)run.TotalMilliseconds;
            row.WallMs = _bundleClock?.ElapsedMilliseconds ?? 0;
            Process_.EmitMs += row.EmitMs;
            Process_.CompileMs += row.CompileMs;
            Process_.RunMs += row.RunMs;
            _bundle = null;
            _bundleClock = null;
        }
        // App rows first, then the bundle row that aggregates them.
        foreach (var a in apps) Append(Path_!, a.ToJsonLine());
        Append(Path_!, row.ToJsonLine());
    }

    private static void WriteProcessRecord()
    {
        if (!Enabled) return;
        PhaseLogRecord row;
        lock (Gate)
        {
            row = Process_;
            row.ExitCode = Environment.ExitCode;
            row.PeakRssBytes = PeakRssBytes();
            // Measured from OS process start, so it includes host startup and the
            // full-opt JIT that <TieredCompilation>false</TieredCompilation> forces —
            // exactly the residual #1825 wants to size.
            try
            {
                using var self = System.Diagnostics.Process.GetCurrentProcess();
                var startUtc = self.StartTime.ToUniversalTime();
                row.WallMs = (long)(DateTime.UtcNow - startUtc).TotalMilliseconds;
                row.StartMs = new DateTimeOffset(startUtc, TimeSpan.Zero).ToUnixTimeMilliseconds();
            }
            catch
            {
                row.WallMs = Environment.TickCount64;
                row.StartMs = NowMs() - row.WallMs;
            }
        }
        Append(Path_!, row.ToJsonLine());
    }

    /// <summary>Unix-epoch milliseconds — the single axis every row's interval is placed on.</summary>
    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>
    /// This process's resident-set high-water mark in bytes. Linux reads VmHWM from
    /// /proc/self/status — the kernel's own high-water mark, which .NET's
    /// PeakWorkingSet64 does not surface on Unix. Falls back to the framework
    /// property elsewhere.
    /// </summary>
    public static long PeakRssBytes()
    {
        try
        {
            if (OperatingSystem.IsLinux() && File.Exists("/proc/self/status"))
            {
                foreach (var line in File.ReadLines("/proc/self/status"))
                {
                    if (!line.StartsWith("VmHWM:", StringComparison.Ordinal)) continue;
                    var kb = line.AsSpan(6).Trim().ToString().Split(' ')[0];
                    if (long.TryParse(kb, out var v)) return v * 1024;
                }
            }
            using var self = System.Diagnostics.Process.GetCurrentProcess();
            return self.PeakWorkingSet64;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Appends one complete record under an exclusive open, so concurrent writers in
    /// any number of processes cannot lose or tear each other's lines.
    ///
    /// FileShare.None, not the obvious FileShare.ReadWrite, and this is measured, not
    /// defensive. `FileMode.Append` is NOT an atomic append in .NET on Linux: the
    /// FileStream tracks its own offset and writes through pwrite at it, so two
    /// handles opened at the same end-of-file overwrite each other. 8 writers x 60
    /// records with FileShare.ReadWrite produced 336 of 480 lines on this machine —
    /// a 30% silent loss, which in a percentile aggregate reads as a runner that is
    /// faster than it is. The same loop with FileShare.None + retry produced 480 of
    /// 480. On Unix .NET backs FileShare.None with flock(LOCK_EX), which is
    /// cross-process (and cross-handle within a process); on Windows it is the
    /// native share mode. Either way exactly one writer holds the file at a time, so
    /// each open positions at the true end.
    ///
    /// The retry budget is generous relative to the work it guards (a sub-millisecond
    /// write, a handful of records per process) and bounded so a stuck holder degrades
    /// to a reported miss rather than a hang.
    ///
    /// A failure here costs the measurement, never the run: this is a diagnostic
    /// bolted onto a test runner, and taking a CI leg down because
    /// AL_RUNNER_PHASE_LOG pointed somewhere unwritable would be strictly worse than
    /// losing the numbers. It is still reported on stderr rather than swallowed.
    /// </summary>
    public static void Append(string path, string line)
    {
        const int maxAttempts = 2000;
        IOException? last = null;
        try
        {
            var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var bytes = Encoding.UTF8.GetBytes(line);
            for (var attempt = 0; attempt < maxAttempts; attempt++)
            {
                try
                {
                    using var fs = new FileStream(path, new FileStreamOptions
                    {
                        Mode = FileMode.Append,
                        Access = FileAccess.Write,
                        Share = FileShare.None,
                        BufferSize = 0, // unbuffered: one Write == one write syscall
                    });
                    fs.Write(bytes, 0, bytes.Length);
                    return;
                }
                catch (IOException ex) when (ex is not (FileNotFoundException or DirectoryNotFoundException))
                {
                    // Another writer holds the exclusive lock. Back off and retry.
                    last = ex;
                    Thread.Sleep(1);
                }
            }
            throw last!;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[phase-log] AL_RUNNER_PHASE_LOG write to '{path}' failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
