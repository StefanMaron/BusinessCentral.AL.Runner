// TestDataProvisioner — the POLICY half of --test-data (issue #2258): which tables get
// hydrated, from which backup, for which company, and when.
//
// The mechanism (decoded value -> NavValue -> in-memory store) lives in
// RecordPatches.TestDataHydration.cs and knows nothing about any of this.
//
// THE POLICY IS ON-DEMAND, PER TABLE (issue #2262)
//   Arm() builds the plan and installs a loader on RecordPatches.GetDataAccessForTableCore —
//   the per-table choke point the virtual tables (AllObj, Field, ...) already use. A table's
//   rows are read out of the backup the first time anything in the run materialises that
//   table's storage, and not before.
//
//   #2258 shipped the eager policy that preceded this, and it is worth recording WHY that
//   was not merely slower. RestoreInstallBaselineSnapshot re-inserts every baseline row at
//   EVERY test boundary — per codeunit under TestIsolation = Codeunit, per test under
//   TestIsolation = Test. Baseline size is therefore a cost paid per boundary, not per run.
//   Measured on the tests/test-data-fixture bundle before this change: hydration put 37,710
//   rows into a 71,389-row install baseline and each boundary restore cost 105-203 ms. On a
//   thousand-test suite under Test isolation that is minutes spent re-inserting rows no test
//   ever reads, which is what made --test-data unusable on a real suite.
//
// THERE IS NO "ALREADY LOADED" FLAG, AND THERE MUST NOT BE ONE
//   GetDataAccessForTableCore only reaches its create-fresh-storage path when the source's
//   perTable LACKS the table, and RestoreInstallBaselineSnapshot repopulates perTable from
//   exactly the snapshot it restores (RecordPatches.InstallBaseline.cs,
//   `perTable[table.TableId] = dataAccess`). So "storage is absent" is already, at every
//   instant and with no window, the same question as "the snapshot the store was last
//   restored from did not carry this table" — which is precisely when a load is needed. A
//   separate HashSet of loaded table ids would be a second copy of that answer, free to drift
//   from it; storage presence cannot drift from itself.
//
//   The two objections raised against snapshot-membership-as-the-flag both dissolve once the
//   question is storage presence:
//     - Pre-capture window. Nothing clears perTable inside RunDependenciesOnly(); the only
//       reset is ResetPerTestState() at the top of the whole install-seed block, before it.
//       A table touched during dependency install loads once, perTable holds it for the rest
//       of the window, and CaptureInstallBaselineSnapshot() picks the rows up by walking the
//       same store. No snapshot needs to exist yet.
//     - App-group boundary. _installBaseline outlives an app group, but the loader never
//       consults it. ResetPerTestState() clears every source's perTable, so group 2's
//       perTable reflects the dep+company snapshot it was actually restored from: a table
//       that snapshot lacks is absent, reloads on touch, and is correct.
//
// A LOAD OUTSIDE THE CAPTURE WINDOW MUST BE WRITTEN INTO THE BASELINES
//   A load fired mid-test happens long after CaptureInstallBaselineSnapshot() walked the
//   store, so the rows are invisible to every snapshot and the very next boundary would drop
//   them. RecordPatches.AppendBaselineTable puts the PRISTINE rows — deep-copied before the
//   triggering test can mutate them — into both the per-app-group singleton and the
//   dep+company snapshot this group is running against. See its doc comment.
//
//   NOT into the PERSISTED disk snapshot, deliberately. That artifact is written once, at the
//   dep+company cache MISS, before any test body runs, so a test-driven load cannot reach it
//   without new code — and adding that code would reintroduce the eager problem in slow
//   motion: the file would grow according to whichever suite happened to run first, and a
//   later, narrower run would inherit rows it never touches and pay to re-insert them at
//   every boundary. Leaving it out is safe for exactly the reason the whole design is safe:
//   a table missing from a snapshot loads on touch. Tables the dependency install triggers /
//   Company-Initialize touch ARE persisted, with no special handling, because they load
//   inside the capture window and CaptureInstallBaselineSnapshot() walks the same store.
//
// ORDERING
//   Arm() runs where HydrateAll() used to, in front of the dependency install triggers, so a
//   table an install trigger reads is already backed by the backup's rows when it reads it —
//   the ordering real BC has, where the database with its data exists before any extension is
//   installed.
//
// TABLE-EXTENSION DATA IS MERGED (issue #2261)
//   BC splits an extended table across the base table and a `$ext` companion. Every read here
//   passes `--merge-extensions`, so the base and companion rows arrive joined, and a table
//   with extension data is no longer skipped whole.
//
//   The request key is `merge-extensions`, HYPHENATED. On reader builds up to and including
//   9701b04 the camelCase spelling `--mergeExtensions` was accepted by the CLI, ignored, and
//   exited 0 — measured, not assumed — which would hydrate Source Code Setup with one of its
//   ~50 fields and report success, and 68 CRONUS tables with it. Reader a431ee4 (BakReader#18)
//   refuses an option the command does not accept, so that specific spelling now exits 1
//   naming the accepted options. Stated as history, not as current reader behaviour.
//
//   AssertMergeIsHonoured() below stays, and the upstream fix is not a reason to remove it.
//   It removed the reason the probe was WRITTEN, not the reason it should remain: the runner
//   does not pin a reader version, and it cannot control which binary is on a user's PATH or
//   on AL_RUNNER_BCBAK. An older reader that silently ignores the flag is still a reachable
//   configuration, and this probe is the only thing that catches it. It reads one extended
//   table BOTH ways before anything is hydrated and requires the merged read to return
//   strictly more columns, so a merge that is not happening fails the run instead of emptying
//   68 tables quietly.
//
//   That check is once per run, not per table, and it has to be: whether the flag is honoured
//   is a property of the reader, not of a table. The per-table form was tried and is wrong —
//   the runner's own NCLMetaField.IsCompanionTableField is false even for a field the backup
//   really does store in the companion (measured on `Return Reason`.`Default Location Code`),
//   so asking our metadata "did an extension column arrive" refuses tables that merged fine.
//   The end-to-end fixture asserts an extension field's VALUE for the same reason.
//
// THIS SLICE'S DECLARED EXCLUSIONS — reported, never silent
//   1. Tables whose AL name is ambiguous in the backup — two installed apps may each declare
//      a table of the same name (namespaces make that legal; Base Application's
//      "Dimension Set Entry" and Power BI Report embeddings' are the shipped example). The
//      reader refuses the name, and so does this: picking whichever candidate has rows would
//      be exactly the silent guess this feature exists to prevent. #2264 resolves it properly,
//      by naming the owning app the runner's own closure already resolved.
//   2. Value types this runner build cannot rebuild yet — refused per table by the mechanism,
//      counted and reported here. This used to gate most of the data (every extended CRONUS
//      table carries a Date somewhere), and no longer does: #2259 took Date/DateTime/Time/
//      DateFormula, #2270 took Blob/Media/MediaSet/RecordId/Duration and #2268 took a DB NULL
//      in any column type. Measured on BC 28.1's W1 CRONUS, all 12 remaining refusals are
//      case 5 below, not a value type at all. TableFilter is the one type left — #2271.
//   3. Tables the READER itself fails on. Reported per table, with the reader's own text, and
//      NEVER fatal to the rest of the hydration. No table is currently known to fail this way,
//      and the tolerance is not speculative: before it existed, one reader exit-1 on a single
//      table aborted the whole hydration and the bundle reported COMPILE FAIL / 0 tests, with
//      the reader's own diagnosis truncated away by the one-line EXEC-FAIL reporter. One
//      unavailable table must not cost the run every other table, or the diagnosis.
//   4. Table-extension columns owned by an app OUTSIDE this run's closure. The reader has no
//      symbols to name them with and passes them through in BC's raw `<name>$<app id>` storage
//      form; this run's AL record has no such field, so they are dropped and counted. See
//      RecordPatches.TestDataHydration's header, case (a).
//   5. A column naming a field the target AL table does not have in THIS build. Dropped and
//      counted under its own name, separately from case 4 (#2273/#2301). Refusing the table
//      was measured to be the more dangerous answer: it leaves the table empty, and an empty
//      table is a state AL reads and believes. `No. Series Line` refused over
//      `Allow Gaps in Nos.` (ObsoleteState = Removed since BC 27.0 — compiled out of the app,
//      still in the shipped symbols and still a physical column), and ~220 of Microsoft's
//      Tests-SINGLESERVER tests then failed on number series the backup says are almost
//      untouched. What was reported as one refusal cost every No. Series in the run.
//      The 11 other tables #2273 listed (Item."Routing No_", Purchase Line."Prod_ Order No_",
//      …) had a different cause and are fixed in the reader, not here: those fields are LIVE,
//      declared by a tableextension in the same app as the table it extends, which BC stores
//      in the base table itself. The reader named base columns from the table's own field
//      list only, so they arrived in SQL form and matched nothing (BakReader:
//      SymbolStore.FindBaseTableField).
//   6. A row shape sharing NO column with the target table. Still refused: that is a
//      mismatch, and hydrating it would insert rows made entirely of defaults.
using AlRunner.Infrastructure;
using AlRunner.Patches;
using System.Text.Json;

namespace AlRunner;

internal static class TestDataProvisioner
{
    internal sealed record Summary(
        string BackupPath, string Company, int TablesHydrated, int RowsHydrated,
        int TablesSkippedAmbiguous, int TablesRefused, int TablesRefusedByReader,
        int ColumnsFromUninstalledApps, int ColumnsNotInThisBuild)
    {
        // "the run touched", not "the backup holds": under the on-demand policy (#2262) a
        // table is only read when something asks for it, so these counts describe what this
        // suite actually pulled in. A small number here is the feature working, not a gap.
        internal string Describe() =>
            $"[test-data] loaded {RowsHydrated} row(s) in {TablesHydrated} table(s) this run touched, "
            + $"from '{Path.GetFileName(BackupPath)}' company '{Company}'; "
            + $"skipped {TablesSkippedAmbiguous} ambiguous by name, "
            + $"{TablesRefused} refused (unsupported value types or unknown columns), "
            + $"{TablesRefusedByReader} refused by the backup reader, "
            + $"{ColumnsFromUninstalledApps} extension column(s) dropped for apps this run does not install, "
            + $"{ColumnsNotInThisBuild} column(s) dropped that this build's AL tables have no field for.";
    }

    private static Summary? _lastSummary;

    /// <summary>The armed plan: everything the on-demand loader needs, resolved once. Null
    /// when --test-data is off or Arm() has not run, which is what makes the loader a no-op
    /// for every default run.</summary>
    private sealed record ArmedPlan(
        string Backup, string SymbolKey, IReadOnlyList<string> Symbols, string Company,
        IReadOnlyDictionary<int, BackupTableEntry> ByTableId, int SkippedAmbiguous);

    private static ArmedPlan? _armed;

    /// <summary>
    /// The running --test-data tallies, accumulated across on-demand loads rather than known at
    /// Arm() time.
    ///
    /// #2997: mutated ONLY through Interlocked, never `++` or `+=`. Two threads can be inside
    /// LoadOnDemand at once — TestExecutor.InvokeWithTimeout runs every [Test] on its own worker
    /// thread and does not kill it when the watchdog expires, so an abandoned thread keeps
    /// hydrating while the bundle loop carries on in the same process (the route #2914
    /// established) — and a read-modify-write from two threads drops counts. Measured: eight
    /// threads doing 10,000 write-offs each landed 79,543 of 80,000.
    ///
    /// #3025: the counts are held as ONE immutable value, swapped by CompareExchange, because
    /// six individually-atomic counters still cannot be READ as a set. Each field was fresh and
    /// each was true; the combination was not. A reader that took the table count, lost its
    /// slice to a thread finishing a table, then took the row count, printed rows belonging to a
    /// table it had not counted — at worst "loaded 4200 row(s) in 0 table(s)", which is a
    /// sentence describing no instant that ever existed. This is a diagnostic a person uses to
    /// decide whether their test data loaded, so a summary that contradicts itself sends them
    /// hunting a bug that is not there. Note this was never a TORN read in the ECMA-335 sense —
    /// aligned int32s are read and written atomically (I.12.6.6) — it was a torn SET.
    ///
    /// The same argument applies to the write side, which is why one table's hydration is one
    /// update rather than four: publishing the table count and the row count separately leaves a
    /// window in which the two disagree, and no reader, however careful, can be prevented from
    /// landing in it.
    ///
    /// Not a lock: readers never block, and a writer's retry loop only spins when it actually
    /// lost a race, which needs two threads inside LoadOnDemand at the same moment.
    ///
    /// An instance rather than six statics so the concurrency claim is provable on a board no
    /// other test in the assembly can reach; the production board is the static below.
    /// </summary>
    internal sealed class TallyBoard
    {
        /// <summary>All six counts as ONE immutable value. Six separate fields cannot be read
        /// as a set — see the class comment — and the cheapest thing that can is an object whose
        /// reference is swapped atomically.</summary>
        private sealed record Counts(
            int Tables, int Rows, int Refused, int ReaderRefused,
            int DroppedColumns, int ColumnsNotInThisBuild)
        {
            internal static readonly Counts Zero = new(0, 0, 0, 0, 0, 0);
        }

        private Counts _counts = Counts.Zero;

        /// <summary>
        /// Apply one event to the counts. Lock-free: read the current value, derive the next
        /// one, and publish it only if nothing else got there first — otherwise start again with
        /// what the winner wrote. Every publication is a whole consistent set, so a reader is
        /// never handed a state that was half-applied.
        ///
        /// The retry loop is unbounded on purpose: it makes progress whenever any thread does,
        /// and the contention it is written for is two threads, once per table load.
        /// </summary>
        private void Update(Func<Counts, Counts> next)
        {
            while (true)
            {
                var before = Volatile.Read(ref _counts);
                if (ReferenceEquals(Interlocked.CompareExchange(ref _counts, next(before), before), before))
                    return;
            }
        }

        /// <summary>One table's successful hydration, applied as a SINGLE update. A table the
        /// backup holds but that has no rows is not a table hydrated, yet its dropped columns
        /// are still real and counted — which is why this takes the row count rather than a
        /// "did it work" flag, and why the table count and the row count have to move together:
        /// "rows counted implies the table that produced them counted" is only an invariant if
        /// nothing can observe the gap between them.</summary>
        internal void NoteHydrated(int rows, int droppedColumns, int columnsNotInThisBuild)
            => Update(c => c with
            {
                Tables = c.Tables + (rows > 0 ? 1 : 0),
                Rows = c.Rows + rows,
                DroppedColumns = c.DroppedColumns + droppedColumns,
                ColumnsNotInThisBuild = c.ColumnsNotInThisBuild + columnsNotInThisBuild,
            });

        /// <summary>One table whose rows the hydrator refused to rebuild.</summary>
        internal void NoteRefused() => Update(c => c with { Refused = c.Refused + 1 });

        /// <summary>One table the backup reader itself refused.</summary>
        internal void NoteReaderRefused() => Update(c => c with { ReaderRefused = c.ReaderRefused + 1 });

        /// <summary>The tallies, read out as the summary a person sees. ONE read of ONE
        /// reference, so the six numbers below are the six numbers that were true together at
        /// one instant — which is the whole point of #3025.</summary>
        internal Summary Capture(string backupPath, string company, int skippedAmbiguous)
        {
            var c = Volatile.Read(ref _counts);
            return new Summary(backupPath, company, c.Tables, c.Rows,
                skippedAmbiguous, c.Refused, c.ReaderRefused,
                c.DroppedColumns, c.ColumnsNotInThisBuild);
        }
    }

    private static TallyBoard _tallies = new();

    /// <summary>Tables the backup offers that ended the run without their rows because the
    /// deferred load (#2877) could not be run safely. Read by the proving test, so "written off"
    /// is an assertable count rather than something inferred from log text.</summary>
    private static int _deferredLoadsWrittenOff;

    internal static int DeferredLoadsWrittenOff => Volatile.Read(ref _deferredLoadsWrittenOff);

    /// <summary>The most recent hydration's outcome — the assertable signal a test uses
    /// instead of re-deriving what happened from log text. Under the on-demand policy it is
    /// cumulative: it reflects every table loaded so far, and it stays null until the first
    /// table actually loads.</summary>
    internal static Summary? LastSummary => _lastSummary;

    /// <summary>Table ids the plan says this run COULD hydrate. The proving test for "a table
    /// nothing touches is never loaded" needs both halves — that the table was in scope, and
    /// that its rows are nonetheless absent — or it would pass against a plan that simply
    /// never knew about the table.</summary>
    internal static IReadOnlyCollection<int> ArmedTableIds
        => _armed?.ByTableId.Keys.ToArray() ?? Array.Empty<int>();

    // ─────────────────────────────────────────── per-table outcome (#2240) ──

    /// <summary>
    /// Why one table ended up with the rows it has, in the user's words rather than the
    /// hydrator's. Recorded per table id by the on-demand loader, and read only by the
    /// missing-test-data diagnosis (Infrastructure.MissingTestDataDiagnosis) when it has
    /// already established that the table is EMPTY and --test-data was on — i.e. the one case
    /// where the user cannot possibly guess which of "refused", "not in this backup" or
    /// "genuinely empty in the backup" happened, and the runner can.
    ///
    /// The aggregate Summary above cannot answer this: it counts refusals across the whole run
    /// and never says which table each one was.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> _tableOutcome = new();

    /// <summary>The recorded outcome for <paramref name="tableId"/>, or null when the loader
    /// never considered it. Null is meaningful: under the on-demand policy it means nothing in
    /// this run ever touched the table, so no reason exists yet.</summary>
    internal static string? TableOutcome(int tableId)
        => _tableOutcome.TryGetValue(tableId, out var reason) ? reason : null;

    // ─────────────────────────────── deferred loads, and their outcomes (#2877) ──

    /// <summary>
    /// A table's storage was created from inside ANOTHER table's --test-data hydration, where a
    /// load would recurse, so nothing has been loaded into it yet.
    ///
    /// Recorded rather than skipped for one reason: without it <see cref="TableOutcome"/>
    /// answers null for that table, and null under the on-demand policy means "nothing in this
    /// run ever touched it" — the exact opposite of what happened, and indistinguishable from a
    /// table nobody asked for. Replaced by the load's own outcome as soon as the debt is
    /// settled, so it is only ever the answer while the debt actually stands.
    /// </summary>
    private static void NoteDeferredLoad(int tableId)
    {
        var armed = _armed;
        if (armed == null) return;
        // A table the plan does not offer owes nothing worth reporting as deferred: the honest
        // answer is the one LoadOnDemand would give, and saying "not loaded yet" for a table
        // this backup does not have would read as a problem where there is none. Every runner
        // test table and every table the company has no rows for lands here.
        if (!armed.ByTableId.ContainsKey(tableId))
        {
            _tableOutcome[tableId] = $"the plan built from '{Path.GetFileName(armed.Backup)}' "
                + $"company '{armed.Company}' has no table with this id, so nothing was loaded";
            return;
        }
        _tableOutcome[tableId] = "its storage was first created from inside another table's "
            + "hydration, where loading it would have recursed, so nothing has been loaded into "
            + $"it yet from '{Path.GetFileName(armed.Backup)}' company '{armed.Company}'; the "
            + "next touch outside a hydration loads it";
        PerfTrace.Log($"TestData.DeferredLoad {tableId}");
    }

    /// <summary>
    /// The deferred load could not be run after all — the store had rows by the time one could,
    /// or the runner could not read whether it did. The table ends the run WITHOUT its backup
    /// rows, which is a fact about the run and is reported as one: recorded per table AND
    /// printed, never dropped quietly (.claude/rules/loud-failures.md).
    ///
    /// Not a throw. The rows it would name are BC's own metadata construction reaching a Record
    /// mid-hydration, and turning that into a hard failure would take out a --test-data run that
    /// works today for the sake of one table — the same trade #2875 rejects for the same reason.
    /// </summary>
    private static void NoteDeferredLoadWrittenOff(int tableId, string reason)
    {
        var armed = _armed;
        if (armed == null) return;
        // Same reason as above: a table this backup does not offer loses nothing by not being
        // loaded, so it must not be printed as though rows went missing.
        if (!armed.ByTableId.ContainsKey(tableId))
        {
            _tableOutcome[tableId] = $"the plan built from '{Path.GetFileName(armed.Backup)}' "
                + $"company '{armed.Company}' has no table with this id, so nothing was loaded";
            return;
        }
        Interlocked.Increment(ref _deferredLoadsWrittenOff);
        _tableOutcome[tableId] = "its storage was first created from inside another table's "
            + $"--test-data hydration and {reason}, so the rows "
            + $"'{Path.GetFileName(armed.Backup)}' company '{armed.Company}' holds for it were "
            + "never loaded";
        Console.Error.WriteLine(
            $"[test-data] NOT LOADED table {tableId}: {reason}. Its storage was first created "
            + "from inside another table's hydration; see issue #2877.");
    }

    /// <summary>True when a plan is armed — i.e. --test-data resolved a backup and a company
    /// and the on-demand loader is installed. Distinguishes "the flag is on and working" from
    /// "the flag is on but Arm() has not run for this app group yet".</summary>
    internal static bool IsArmed => _armed != null;

    /// <summary>The armed backup/company pair, for a diagnosis that has to name them.
    ///
    /// #3025, same shape as the summary: read the field ONCE. `_armed == null ? null :
    /// (_armed.Backup, _armed.Company)` loads it three times, and the bundle loop re-arms or
    /// resets it between groups while an abandoned hydration thread is still running (#2914).
    /// Three loads can therefore null-reference after a null check that just passed, or pair one
    /// plan's backup with the next plan's company — a diagnosis naming a backup/company
    /// combination that was never armed.</summary>
    internal static (string Backup, string Company)? ArmedBackup
    {
        get
        {
            var armed = _armed;
            return armed == null ? null : (armed.Backup, armed.Company);
        }
    }

    internal static void ResetForTests()
    {
        _lastSummary = null;
        _armed = null;
        // A fresh board rather than six stores into the old one: one reference store, so a
        // reader that is still running cannot be handed a half-cleared set of tallies.
        _tallies = new TallyBoard();
        // Plain store on purpose (#2997): the reset runs between runs with no other thread in
        // play, and routing it through Interlocked would imply a concurrency it does not have.
        _deferredLoadsWrittenOff = 0;
        _tableOutcome.Clear();
        RecordPatches.TestDataOnDemandLoader = null;
        RecordPatches.TestDataDeferredLoadNotifier = null;
        RecordPatches.TestDataDeferredLoadWriteOffNotifier = null;
        // #2875: the provenance record describes the backup this loader was armed for, so it
        // has to go with the loader. Left behind, one run's backup would keep speaking for the
        // next — and the thing it says is "do not synthesise rows for this table".
        RecordPatches.ResetBackupRowProvenance();
    }

    /// <summary>
    /// Resolve the backup, the company and the table plan, and install the on-demand loader.
    /// Reads no table rows: the first touch of a table does that. A no-op when --test-data
    /// was not passed, so nothing about a default run changes.
    /// </summary>
    internal static void Arm()
    {
        if (!TestDataOptions.Enabled) return;

        var backup = TestDataOptions.ResolveBackupPath();
        var symbols = ResolveSymbols();
        // Idempotent per SYMBOL SET, not per process. Arm() is now called once per app group
        // (it has to be: a run whose first group takes a cache HIT would otherwise never arm
        // at all), and re-reading the catalog per group would cost three reader invocations
        // each. Keying on the symbols rather than skipping outright is the correctness half:
        // which AL table id a backup table maps to is decided by the .app closure the reader
        // was given, so a group with a different closure needs its own plan.
        var symbolKey = string.Join('\n', symbols);
        // One read, for the reason in ArmedBackup: three loads can test three different plans,
        // and "already armed for this backup" would then be true of no plan in particular.
        var armed = _armed;
        if (armed != null && armed.SymbolKey == symbolKey && armed.Backup == backup)
        {
            // The loader is a static field; re-install it in case something cleared it.
            InstallLoader();
            return;
        }

        var company = ResolveCompany(backup);

        var tablesOutput = BackupReaderTool.Run(SymbolArgs(new[] { "tables", backup }, symbols));
        var entries = BackupCatalog.ParseTables(tablesOutput);

        var plan = BuildPlan(entries, company);
        Console.Error.WriteLine(
            $"[test-data] backup '{backup}', company '{company}', {plan.Hydratable.Count} table(s) in scope "
            + $"({plan.ExtendedTableNames.Count} with table-extension data to merge, "
            + $"{plan.SkippedAmbiguous} ambiguous by name); loading on first touch.");

        // BEFORE any hydration: prove the reader is actually merging. A run that got this
        // wrong would hydrate every extended table with its extension fields blank and report
        // success, so this is fatal rather than per-table.
        //
        // ONCE PER RUN, not once per table, and the move to on-demand loading does not change
        // that: whether the reader honours the flag is a property of the READER, not of a
        // table, so asking it again per table would buy nothing and cost two extra reader
        // invocations per loaded table. It stays here, at arm time, where it still runs before
        // any row is hydrated.
        if (plan.ExtendedTableNames.Count > 0)
            AssertMergeIsHonoured(backup, symbols, company,
                plan.ExtendedTableNames.OrderBy(n => n, StringComparer.Ordinal).First());

        var byTableId = new Dictionary<int, BackupTableEntry>();
        foreach (var e in plan.Hydratable)
            if (e.AlTableId != null)
                byTableId[e.AlTableId.Value] = e;

        _armed = new ArmedPlan(backup, symbolKey, symbols, company, byTableId, plan.SkippedAmbiguous);
        InstallLoader();
    }

    /// <summary>The three hooks the materialisation path calls back on, installed together so a
    /// re-arm can never leave the loader in place with the reporting halves cleared.</summary>
    private static void InstallLoader()
    {
        RecordPatches.TestDataOnDemandLoader = LoadOnDemand;
        RecordPatches.TestDataDeferredLoadNotifier = NoteDeferredLoad;
        RecordPatches.TestDataDeferredLoadWriteOffNotifier = NoteDeferredLoadWrittenOff;
    }

    /// <summary>
    /// The on-demand load, called by RecordPatches.GetDataAccessForTableCore the moment it
    /// creates fresh storage for <paramref name="tableId"/> on <paramref name="source"/> —
    /// i.e. exactly when the store does not have the table and therefore needs it.
    ///
    /// Runs BEFORE the operation that triggered it, so reads and writes alike see the backup's
    /// rows. That symmetry is the reason the hook is here and not on the read path: an AL
    /// Insert of a primary key the backup already holds must fail with a duplicate-key error,
    /// the way it would on real BC with the row present, and a load-on-read design gets that
    /// case silently wrong.
    ///
    /// Never throws. A table this build cannot rebuild is reported and left empty — the same
    /// per-table refusal the eager policy had, just reported at the moment of the touch.
    /// </summary>
    private static void LoadOnDemand(object source, int tableId)
    {
        var armed = _armed;
        if (armed == null) return;
        if (!armed.ByTableId.TryGetValue(tableId, out var entry))
        {
            // #2240: recorded, not just skipped. "This backup's plan does not offer the table"
            // is one of the answers the diagnosis has to be able to give, and it is only
            // knowable here — the store afterwards looks the same as a refusal.
            _tableOutcome[tableId] = $"the plan built from '{Path.GetFileName(armed.Backup)}' "
                + $"company '{armed.Company}' has no table with this id, so nothing was loaded";
            return;
        }

        try
        {
            var result = HydrateOne(armed.Backup, armed.Symbols, armed.Company, entry, source,
                out var meta, out var pristineRows);
            if (result.Rows > 0)
            {
                // #2875: say so, rather than leaving anyone downstream to infer it from the
                // store. A table the runner can ALSO synthesise rows for (Object 2000000001)
                // has to know which writer owns its rows, and "the provider has rows" cannot
                // answer that once an install-baseline restore can replay a projection into a
                // brand-new provider. This is the only place the fact exists.
                //
                // BEFORE the append, deliberately: AppendBaselineTable refuses a
                // projection-owned table, and this call is what makes this one not projection
                // owned. See RecordPatches.BackupRowProvenance.cs.
                RecordPatches.NoteBackupContributedRows(tableId);
                // The rows are in the live store now, but no snapshot knows about them: a load
                // fired mid-test is long past CaptureInstallBaselineSnapshot(). Without this
                // the very next codeunit/test boundary would wipe them.
                if (meta != null)
                    RecordPatches.AppendBaselineTable(source, tableId, meta, pristineRows);
            }
            _tallies.NoteHydrated(result.Rows, result.ColumnsFromUninstalledApps, result.ColumnsNotInThisBuild);
            _tableOutcome[tableId] = result.Rows > 0
                ? $"{result.Rows} row(s) loaded from '{Path.GetFileName(armed.Backup)}' company '{armed.Company}'"
                : $"'{entry.TableName}' in '{Path.GetFileName(armed.Backup)}' company '{armed.Company}' "
                  + "holds no rows, so there was nothing to load";
            PerfTrace.Log(
                $"TestData.LazyLoad {tableId} '{entry.TableName}' {result.Rows} row(s)");
        }
        catch (TestDataHydrationRefusal ex)
        {
            _tallies.NoteRefused();
            _tableOutcome[tableId] = $"the backup's rows for it were refused — {ex.Message}";
            Console.Error.WriteLine($"[test-data] REFUSED {ex.Message}");
        }
        catch (BackupReaderException ex)
        {
            // The reader failing on ONE table must not cost the run every other table: that is
            // a table that is unavailable, not a run that is broken. Reported with the reader's
            // own text IN FULL, because that text is the only diagnosis there is and the bundle
            // reporter keeps only line 1 of an EXEC-FAIL message.
            _tallies.NoteReaderRefused();
            _tableOutcome[tableId] =
                $"the backup reader refused table '{entry.TableName}' — {ex.Message.Split('\n')[0]}";
            Console.Error.WriteLine(
                $"[test-data] READER REFUSED table '{entry.TableName}': {ex.Message}");
        }
        _lastSummary = _tallies.Capture(armed.Backup, armed.Company, armed.SkippedAmbiguous);
    }

    /// <summary>
    /// Hydrate ONE table. The unit a future on-demand policy would call; the eager loop above
    /// is only a caller of it. Returns the rows inserted and the extension columns dropped, or
    /// throws <see cref="TestDataHydrationRefusal"/> naming the table, column and type it could
    /// not rebuild — never a partial table.
    ///
    /// </summary>
    internal static RecordPatches.TestDataTableResult HydrateOne(
        string backup, IReadOnlyList<string> symbols, string company, BackupTableEntry entry)
        => HydrateOne(backup, symbols, company, entry, null, out _, out _);

    internal static RecordPatches.TestDataTableResult HydrateOne(
        string backup, IReadOnlyList<string> symbols, string company, BackupTableEntry entry,
        object? intoSource,
        out Microsoft.Dynamics.Nav.Runtime.NCLMetaTable? metaTable,
        out Microsoft.Dynamics.Nav.Runtime.NavValue[][] pristineRows)
    {
        metaTable = null;
        pristineRows = Array.Empty<Microsoft.Dynamics.Nav.Runtime.NavValue[]>();
        if (entry.AlTableId == null)
            throw new TestDataHydrationRefusal(
                $"table '{entry.TableName}': the run's app closure does not define it, so it has no AL table id");

        var readArgs = SymbolArgs(
            new[]
            {
                "read", backup, "--table", entry.TableName, "--company", company, "--format", "json",
                // HYPHENATED. `--mergeExtensions` is accepted, ignored, and exits 0.
                "--merge-extensions",
            }, symbols);
        var json = BackupReaderTool.Run(readArgs);

        List<IReadOnlyDictionary<string, System.Text.Json.JsonElement>> rows;
        try { rows = ParseRows(json); }
        catch (JsonException ex)
        {
            throw new TestDataHydrationRefusal(
                $"table '{entry.TableName}': the reader's JSON could not be parsed ({ex.Message})");
        }

        return RecordPatches.HydrateTestDataTable(
            entry.AlTableId.Value, entry.TableName, rows, intoSource, out metaTable, out pristineRows);
    }

    /// <summary>
    /// Read <paramref name="probeTable"/> both without and with `--merge-extensions` and
    /// require the merged read to return strictly more columns. Throws
    /// <see cref="TestDataUnavailableException"/> if it does not.
    ///
    /// One extra read per run buys the one thing nothing else can prove: that the reader is
    /// honouring the flag at all. Getting that wrong is silent by construction — the merged
    /// read simply returns the base table, every extension field hydrates blank, and the run
    /// reports success. The probe table is one the catalog says HAS companion rows, so the
    /// two reads must differ.
    /// </summary>
    internal static void AssertMergeIsHonoured(
        string backup, IReadOnlyList<string> symbols, string company, string probeTable)
    {
        var head = new[] { "read", backup, "--table", probeTable, "--company", company, "--format", "json", "--top", "1" };
        var plain = ParseRows(BackupReaderTool.Run(SymbolArgs(head, symbols)));
        var merged = ParseRows(BackupReaderTool.Run(
            SymbolArgs(head.Append("--merge-extensions").ToArray(), symbols)));
        CompareMergeProbe(
            probeTable,
            plain.Count == 0 ? Array.Empty<string>() : plain[0].Keys.ToArray(),
            merged.Count == 0 ? Array.Empty<string>() : merged[0].Keys.ToArray());
    }

    /// <summary>
    /// The verdict half of <see cref="AssertMergeIsHonoured"/>, pure over the two column sets
    /// so the claim is testable without a 900 MB backup.
    /// </summary>
    internal static void CompareMergeProbe(
        string probeTable, IReadOnlyCollection<string> plainColumns, IReadOnlyCollection<string> mergedColumns)
    {
        var plain = plainColumns.ToHashSet(StringComparer.Ordinal);
        var merged = mergedColumns.ToHashSet(StringComparer.Ordinal);
        if (merged.IsProperSupersetOf(plain)) return;

        // Everything actionable on the FIRST line: the bundle reporter keeps only line 1.
        throw new TestDataUnavailableException(
            $"--test-data: the backup reader is not honouring '--merge-extensions' — reading '{probeTable}', "
            + $"whose '$ext' companion holds rows, returned {merged.Count} column(s) with the flag and "
            + $"{plain.Count} without, so every table-extension field would hydrate blank while the run "
            + "reported success. Check the reader build (AL_RUNNER_BCBAK); the request key is hyphenated, "
            + "and reader builds before a431ee4 accept the camelCase spelling, ignore it, and exit 0.");
    }

    /// <summary>
    /// Project the reader's JSON array into one dictionary per row, keyed by the AL field
    /// NAME the reader emitted. BC's own bookkeeping columns are dropped here (they carry no
    /// AL field the runner will insert into — see RecordPatches.TestDataHydration's header);
    /// every remaining key must resolve against the target metatable, or the table is refused.
    /// </summary>
    internal static List<IReadOnlyDictionary<string, JsonElement>> ParseRows(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var rows = new List<IReadOnlyDictionary<string, JsonElement>>();
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            var row = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var prop in element.EnumerateObject())
            {
                if (RecordPatches.TestDataSystemColumnNames.Contains(prop.Name)) continue;
                row[prop.Name] = prop.Value.Clone();
            }
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>The tables to hydrate, plus the subset whose `$ext` companion carries rows.
    /// The second set is not bookkeeping: it is handed to the mechanism per table as the
    /// requirement "this merged read must come back with an extension column", which is what
    /// makes a merge that did not happen fail instead of hydrating blanks.</summary>
    internal sealed record Plan(
        IReadOnlyList<BackupTableEntry> Hydratable,
        IReadOnlySet<string> ExtendedTableNames,
        int SkippedAmbiguous);

    /// <summary>
    /// Decide which tables this slice hydrates. Pure over the catalog so the exclusion rules
    /// are testable without a backup — they are the part a reader has to be able to check.
    /// </summary>
    internal static Plan BuildPlan(IReadOnlyList<BackupTableEntry> entries, string company)
    {
        var forCompany = entries.Where(e => string.Equals(e.Company, company, StringComparison.Ordinal)).ToList();

        // Base tables whose $ext companion carries rows. Before #2261 these were excluded
        // whole; now they are hydrated WITH the companion merged in, and this set is the
        // per-table assertion that the merge actually ran.
        var extendedBaseNames = forCompany
            .Where(e => e.IsExtensionCompanion && e.RowCount > 0)
            .Select(e => e.BaseTableName)
            .ToHashSet(StringComparer.Ordinal);

        // A (company, table name) appearing more than once is ambiguous (exclusion 1).
        var nameCounts = forCompany
            .Where(e => !e.IsExtensionCompanion)
            .GroupBy(e => e.TableName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var hydratable = new List<BackupTableEntry>();
        var skippedAmbiguous = 0;
        foreach (var e in forCompany)
        {
            if (e.IsExtensionCompanion) continue;
            if (e.RowCount == 0) continue;
            if (e.AlTableId == null) continue;                 // not defined by this run's app closure
            if (nameCounts.TryGetValue(e.TableName, out var n) && n > 1) { skippedAmbiguous++; continue; }
            hydratable.Add(e);
        }
        // Only the tables actually in the plan can carry the requirement; keeping companions of
        // excluded tables in the set would make it read as a claim about tables nobody reads.
        var planned = hydratable.Select(e => e.TableName).ToHashSet(StringComparer.Ordinal);
        extendedBaseNames.IntersectWith(planned);
        return new Plan(hydratable, extendedBaseNames, skippedAmbiguous);
    }

    /// <summary>
    /// The company to hydrate. Repo owner's decision: when the backup holds more than one and
    /// none was named, FAIL — never pick one. A BC backup routinely carries several companies
    /// (the shipped demo database has "CRONUS ..." and "My Company"), they hold different
    /// data, and silently choosing means every row in the run came from a company nobody
    /// selected. That is the same class of silent wrong answer as restoring an empty snapshot.
    /// </summary>
    internal static string ResolveCompany(IReadOnlyList<string> companies, string? overrideName, string backupForDiagnostics)
    {
        // Everything actionable goes on the FIRST line. Measured: the bundle reporter keeps
        // only line 1 of an EXEC-FAIL message, so a message that named a count on line 1 and
        // the companies on line 3 reached the user as "holds 2 companies" with no way to act
        // on it.
        var list = string.Join(", ", companies.Select(c => $"'{c}'"));
        if (overrideName != null)
        {
            if (!companies.Contains(overrideName, StringComparer.Ordinal))
                throw new TestDataUnavailableException(
                    $"--test-data-company '{overrideName}' is not a company in "
                    + $"'{Path.GetFileName(backupForDiagnostics)}', which holds {list}.");
            return overrideName;
        }
        if (companies.Count == 0)
            throw new TestDataUnavailableException(
                $"--test-data: the backup '{backupForDiagnostics}' reports no companies, so there is nothing to hydrate.");
        if (companies.Count > 1)
            throw new TestDataUnavailableException(
                $"--test-data: '{Path.GetFileName(backupForDiagnostics)}' holds {companies.Count} companies "
                + $"({list}) and none was named — pick one with --test-data-company \"<name>\". "
                + "Choosing for you would mean every hydrated row came from a company nobody selected.");
        return companies[0];
    }

    private static string ResolveCompany(string backup)
        => ResolveCompany(
            BackupCatalog.ParseCompanies(BackupReaderTool.Run(new[] { "companies", backup })),
            TestDataOptions.CompanyOverride, backup);

    /// <summary>
    /// The .app packages the reader is told the database's schema comes from: exactly the
    /// closure this run resolved. Not a broader scan — a table the run cannot build an AL
    /// record for is not hydratable, so widening the symbol set would only add tables that
    /// then get refused, at a real per-invocation cost.
    /// </summary>
    private static IReadOnlyList<string> ResolveSymbols()
    {
        var apps = BcCompiler.ResolvedDepAppPaths()
            .Where(p => p.EndsWith(".app", StringComparison.OrdinalIgnoreCase) && File.Exists(p))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        // #2794: a source sibling (app + tests, both from source) is resolved through the
        // runner's own workspace-deps NAVX — manifest + src/*.al, no SymbolReference.json, by
        // design (SiblingCompile). The reader refuses its whole --symbols list on one such
        // entry ("no SymbolReference.json and no single inner .app"), so the run EXEC-FAILed
        // before hydrating anything. Only symbol-bearing packages go to the reader; the rest
        // are named here, because a table that only that package declares cannot be mapped
        // from the backup and a reader of the run should know why.
        var (keep, skipped) = PartitionSymbolPackages(apps, IsReaderConsumable);
        foreach (var s in skipped)
            Console.Error.WriteLine(
                $"[test-data] not handing '{s}' to the backup reader: source-only package (no "
                + "SymbolReference.json), so tables it alone declares cannot be mapped from the backup");

        if (keep.Count == 0)
            throw new TestDataUnavailableException(
                "--test-data: this run resolved no Microsoft/ISV .app dependencies carrying a "
                + "SymbolReference.json, so the backup reader has no symbols to map SQL columns onto "
                + "AL fields with. Point --package-cache at the platform apps for the selected BC version.");
        return keep;
    }

    /// <summary>
    /// True unless the package is POSITIVELY a source-only NAVX — manifest readable, no
    /// SymbolReference.json. A file the runner cannot read as a package at all is kept and
    /// handed to the reader as before: the reader names what is wrong with it loudly, whereas
    /// dropping it here would turn an unreadable dependency into a silent absence (and the
    /// lazy-hydration tests drive this path with a placeholder .app the fake reader never opens).
    /// </summary>
    internal static bool IsReaderConsumable(string appPath)
    {
        var (manifest, hasSymbolReference) = AppLoader.ReadPackageMeta(appPath);
        return manifest == null || hasSymbolReference;
    }

    /// <summary>
    /// Split the resolved .app closure into the packages the reader can consume (they carry a
    /// SymbolReference.json) and the source-only ones it refuses. Order is preserved in both
    /// halves; the predicate is injected so the decision is testable without packages on disk.
    /// </summary>
    internal static (IReadOnlyList<string> Keep, IReadOnlyList<string> Skipped) PartitionSymbolPackages(
        IEnumerable<string> apps, Func<string, bool> hasSymbolReference)
    {
        var keep = new List<string>();
        var skipped = new List<string>();
        foreach (var a in apps)
            (hasSymbolReference(a) ? keep : skipped).Add(a);
        return (keep, skipped);
    }

    private static string[] SymbolArgs(IReadOnlyList<string> head, IReadOnlyList<string> symbols)
        => head.Concat(new[] { "--symbols", string.Join(',', symbols) }).ToArray();
}
