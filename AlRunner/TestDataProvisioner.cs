// TestDataProvisioner — the POLICY half of --test-data (issue #2258): which tables get
// hydrated, from which backup, for which company, and when.
//
// The mechanism (decoded value -> NavValue -> in-memory store) lives in
// RecordPatches.TestDataHydration.cs and knows nothing about any of this. The split is
// load-bearing: hydrating everything up front is right for a CRONUS-sized demo database and
// wrong for a customer backup with millions of rows, where the load has to become per-table
// and on demand. HydrateOne() is already the per-table entry point that a future on-demand
// call site (RecordPatches.GetDataAccessForTableCore, the choke point the virtual tables
// already use) would call; HydrateAll() is only the current eager policy on top of it.
//
// THE EAGER POLICY HERE IS INTERIM. #2262 tracks moving the load to
// RecordPatches.GetDataAccessForTableCore, the per-table choke point the virtual tables
// already use, so the baseline stays proportional to what a suite actually touches. That
// matters because RestoreInstallBaselineSnapshot re-inserts every baseline row at EVERY test
// boundary, so baseline size is a per-boundary cost, not a per-run one.
//
// There is no "already loaded" flag to maintain, and #2262 has the argument: that choke point
// only reaches its create-fresh-storage path when the source's perTable lacks the table, and
// RestoreInstallBaselineSnapshot repopulates perTable from exactly the snapshot it restores
// (RecordPatches.InstallBaseline.cs, `perTable[table.TableId] = dataAccess`). So "storage is
// absent" is already, at every instant, the same question as "the snapshot the store was last
// restored from did not carry this table" — which is precisely when a load is needed.
// Nothing below assumes the eager ordering: HydrateOne() is already the per-table unit such a
// policy would call.
//
// ORDERING (eager policy)
//   Hydrate, THEN run install triggers, THEN run tests. That is the repo owner's stated
//   ordering and it matches real BC, where the database with its data exists before any
//   extension is installed.
//
// THIS SLICE'S DECLARED EXCLUSIONS — reported, never silent
//   1. Table-extension data. BC splits an extended table across the base table and a `$ext`
//      companion whose columns carry no AL field id, so an AL record cannot be rebuilt from
//      them. A base table WITH `$ext` rows is skipped whole rather than hydrated with a
//      knowingly incomplete row set. #2261 lifts this: the reader can now resolve extension
//      fields to their real AL field ids and join them in. Note when doing so that the
//      request key is `merge-extensions`, hyphenated — the camelCase spelling is accepted and
//      SILENTLY IGNORED, which would hydrate Source Code Setup with one of its ~50 fields and
//      report success, so the test for it must assert an extension field's VALUE.
//   2. Tables whose AL name is ambiguous in the backup — two installed apps may each declare
//      a table of the same name (namespaces make that legal; Base Application's
//      "Dimension Set Entry" and Power BI Report embeddings' are the shipped example). The
//      reader refuses the name, and so does this: picking whichever candidate has rows would
//      be exactly the silent guess this feature exists to prevent. #2264 resolves it properly,
//      by naming the owning app the runner's own closure already resolved.
//   3. Value types this runner build cannot rebuild yet (dates, times, BLOBs, media, …) —
//      refused per table by the mechanism, counted and reported here.
using AlRunner.Infrastructure;
using AlRunner.Patches;
using System.Text.Json;

namespace AlRunner;

internal static class TestDataProvisioner
{
    internal sealed record Summary(
        string BackupPath, string Company, int TablesHydrated, int RowsHydrated,
        int TablesSkippedExtensionData, int TablesSkippedAmbiguous, int TablesRefused)
    {
        internal string Describe() =>
            $"[test-data] {RowsHydrated} row(s) in {TablesHydrated} table(s) from '{Path.GetFileName(BackupPath)}' "
            + $"company '{Company}'; skipped {TablesSkippedExtensionData} with extension data, "
            + $"{TablesSkippedAmbiguous} ambiguous by name, {TablesRefused} refused (unsupported value types).";
    }

    private static Summary? _lastSummary;

    /// <summary>The most recent hydration's outcome — the assertable signal a test uses
    /// instead of re-deriving what happened from log text.</summary>
    internal static Summary? LastSummary => _lastSummary;

    internal static void ResetForTests() => _lastSummary = null;

    /// <summary>
    /// The eager policy: hydrate every in-scope table for the selected company. A no-op when
    /// --test-data was not passed, so nothing about a default run changes.
    /// </summary>
    internal static void HydrateAll()
    {
        if (!TestDataOptions.Enabled) return;

        var backup = TestDataOptions.ResolveBackupPath();
        var symbols = ResolveSymbols();
        var company = ResolveCompany(backup);

        var tablesOutput = BackupReaderTool.Run(SymbolArgs(new[] { "tables", backup }, symbols));
        var entries = BackupCatalog.ParseTables(tablesOutput);

        var plan = BuildPlan(entries, company);
        Console.Error.WriteLine(
            $"[test-data] backup '{backup}', company '{company}', {plan.Hydratable.Count} table(s) in scope "
            + $"({plan.SkippedExtensionData} with extension data, {plan.SkippedAmbiguous} ambiguous by name).");

        var tablesDone = 0;
        var rowsDone = 0;
        var refused = 0;
        foreach (var entry in plan.Hydratable)
        {
            try
            {
                var rows = HydrateOne(backup, symbols, company, entry);
                if (rows > 0) tablesDone++;
                rowsDone += rows;
            }
            catch (TestDataHydrationRefusal ex)
            {
                refused++;
                Console.Error.WriteLine($"[test-data] REFUSED {ex.Message}");
            }
        }

        _lastSummary = new Summary(backup, company, tablesDone, rowsDone,
            plan.SkippedExtensionData, plan.SkippedAmbiguous, refused);
        Console.Error.WriteLine(_lastSummary.Describe());
    }

    /// <summary>
    /// Hydrate ONE table. The unit a future on-demand policy would call; the eager loop above
    /// is only a caller of it. Returns the number of rows inserted, or throws
    /// <see cref="TestDataHydrationRefusal"/> naming the table, column and type it could not
    /// rebuild — never a partial table.
    /// </summary>
    internal static int HydrateOne(
        string backup, IReadOnlyList<string> symbols, string company, BackupTableEntry entry)
    {
        if (entry.AlTableId == null)
            throw new TestDataHydrationRefusal(
                $"table '{entry.TableName}': the run's app closure does not define it, so it has no AL table id");

        var readArgs = SymbolArgs(
            new[] { "read", backup, "--table", entry.TableName, "--company", company, "--format", "json" }, symbols);
        var json = BackupReaderTool.Run(readArgs);

        List<IReadOnlyDictionary<string, System.Text.Json.JsonElement>> rows;
        try { rows = ParseRows(json); }
        catch (JsonException ex)
        {
            throw new TestDataHydrationRefusal(
                $"table '{entry.TableName}': the reader's JSON could not be parsed ({ex.Message})");
        }

        return RecordPatches.HydrateTestDataTable(
            entry.AlTableId.Value, entry.TableName, rows);
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

    internal sealed record Plan(
        IReadOnlyList<BackupTableEntry> Hydratable, int SkippedExtensionData, int SkippedAmbiguous);

    /// <summary>
    /// Decide which tables this slice hydrates. Pure over the catalog so the exclusion rules
    /// are testable without a backup — they are the part a reader has to be able to check.
    /// </summary>
    internal static Plan BuildPlan(IReadOnlyList<BackupTableEntry> entries, string company)
    {
        var forCompany = entries.Where(e => string.Equals(e.Company, company, StringComparison.Ordinal)).ToList();

        // Base tables whose $ext companion carries rows: excluded whole (exclusion 1).
        var extendedBaseNames = forCompany
            .Where(e => e.IsExtensionCompanion && e.RowCount > 0)
            .Select(e => e.BaseTableName)
            .ToHashSet(StringComparer.Ordinal);

        // A (company, table name) appearing more than once is ambiguous (exclusion 2).
        var nameCounts = forCompany
            .Where(e => !e.IsExtensionCompanion)
            .GroupBy(e => e.TableName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var hydratable = new List<BackupTableEntry>();
        var skippedExt = 0;
        var skippedAmbiguous = 0;
        foreach (var e in forCompany)
        {
            if (e.IsExtensionCompanion) continue;
            if (e.RowCount == 0) continue;
            if (e.AlTableId == null) continue;                 // not defined by this run's app closure
            if (extendedBaseNames.Contains(e.TableName)) { skippedExt++; continue; }
            if (nameCounts.TryGetValue(e.TableName, out var n) && n > 1) { skippedAmbiguous++; continue; }
            hydratable.Add(e);
        }
        return new Plan(hydratable, skippedExt, skippedAmbiguous);
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
