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
//   5. A BARE column name the target AL table has no field for. Refused, not dropped, and the
//      distinction from case 4 is the point: a bare unresolvable name could equally be a
//      schema mismatch, and hydrating a table against a shape this build does not have is
//      exactly the silent guess the feature exists to prevent. Measured on BC 28.1's W1
//      CRONUS this is now the ONLY refusal left, 12 tables of it (Item."Routing No_",
//      Purchase Line."Prod_ Order No_", …) — #2273.
using AlRunner.Infrastructure;
using AlRunner.Patches;
using System.Text.Json;

namespace AlRunner;

internal static class TestDataProvisioner
{
    internal sealed record Summary(
        string BackupPath, string Company, int TablesHydrated, int RowsHydrated,
        int TablesSkippedAmbiguous, int TablesRefused, int TablesRefusedByReader,
        int ColumnsFromUninstalledApps)
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
            + $"{ColumnsFromUninstalledApps} extension column(s) dropped for apps this run does not install.";
    }

    private static Summary? _lastSummary;

    /// <summary>The armed plan: everything the on-demand loader needs, resolved once. Null
    /// when --test-data is off or Arm() has not run, which is what makes the loader a no-op
    /// for every default run.</summary>
    private sealed record ArmedPlan(
        string Backup, string SymbolKey, IReadOnlyList<string> Symbols, string Company,
        IReadOnlyDictionary<int, BackupTableEntry> ByTableId, int SkippedAmbiguous);

    private static ArmedPlan? _armed;

    // Running tallies, accumulated across on-demand loads rather than known at Arm() time.
    private static int _tablesDone, _rowsDone, _refused, _readerRefused, _droppedColumns;

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

    internal static void ResetForTests()
    {
        _lastSummary = null;
        _armed = null;
        _tablesDone = _rowsDone = _refused = _readerRefused = _droppedColumns = 0;
        RecordPatches.TestDataOnDemandLoader = null;
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
        if (_armed != null && _armed.SymbolKey == symbolKey && _armed.Backup == backup)
        {
            // The loader is a static field; re-install it in case something cleared it.
            RecordPatches.TestDataOnDemandLoader = LoadOnDemand;
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
        RecordPatches.TestDataOnDemandLoader = LoadOnDemand;
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
        if (!armed.ByTableId.TryGetValue(tableId, out var entry)) return;   // not a table this backup offers

        try
        {
            var result = HydrateOne(armed.Backup, armed.Symbols, armed.Company, entry, source,
                out var meta, out var pristineRows);
            if (result.Rows > 0)
            {
                _tablesDone++;
                // The rows are in the live store now, but no snapshot knows about them: a load
                // fired mid-test is long past CaptureInstallBaselineSnapshot(). Without this
                // the very next codeunit/test boundary would wipe them.
                if (meta != null)
                    RecordPatches.AppendBaselineTable(source, tableId, meta, pristineRows);
            }
            _rowsDone += result.Rows;
            _droppedColumns += result.ColumnsFromUninstalledApps;
            PerfTrace.Log(
                $"TestData.LazyLoad {tableId} '{entry.TableName}' {result.Rows} row(s)");
        }
        catch (TestDataHydrationRefusal ex)
        {
            _refused++;
            Console.Error.WriteLine($"[test-data] REFUSED {ex.Message}");
        }
        catch (BackupReaderException ex)
        {
            // The reader failing on ONE table must not cost the run every other table: that is
            // a table that is unavailable, not a run that is broken. Reported with the reader's
            // own text IN FULL, because that text is the only diagnosis there is and the bundle
            // reporter keeps only line 1 of an EXEC-FAIL message.
            _readerRefused++;
            Console.Error.WriteLine(
                $"[test-data] READER REFUSED table '{entry.TableName}': {ex.Message}");
        }
        _lastSummary = new Summary(armed.Backup, armed.Company, _tablesDone, _rowsDone,
            armed.SkippedAmbiguous, _refused, _readerRefused, _droppedColumns);
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
        if (apps.Count == 0)
            throw new TestDataUnavailableException(
                "--test-data: this run resolved no Microsoft/ISV .app dependencies, so the backup reader "
                + "has no symbols to map SQL columns onto AL fields with. Point --package-cache at the "
                + "platform apps for the selected BC version.");
        return apps;
    }

    private static string[] SymbolArgs(IReadOnlyList<string> head, IReadOnlyList<string> symbols)
        => head.Concat(new[] { "--symbols", string.Join(',', symbols) }).ToArray();
}
