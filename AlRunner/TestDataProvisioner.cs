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
//   NOTE for whoever moves it: RecordPatches.ResetPerTestState() clears EVERY table's data
//   access, and RestoreInstallBaselineSnapshot re-creates storage only for tables present in
//   the snapshot. A table hydrated AFTER the baseline was captured is therefore wiped at the
//   next test boundary and reads empty from then on. Moving the load later needs that solved
//   first; it is not solved here, which is why the eager policy is what ships.
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
//      knowingly incomplete row set.
//   2. Tables whose AL name is ambiguous in the backup — two installed apps may each declare
//      a table of the same name (namespaces make that legal; Base Application's
//      "Dimension Set Entry" and Power BI Report embeddings' are the shipped example). The
//      reader refuses the name, and so does this: picking whichever candidate has rows would
//      be exactly the silent guess this feature exists to prevent.
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

        var describeArgs = SymbolArgs(
            new[] { "describe", backup, "--table", entry.TableName, "--company", company }, symbols);
        BackupTableSchema schema;
        try { schema = BackupCatalog.ParseDescribe(BackupReaderTool.Run(describeArgs), entry.TableName); }
        catch (BackupReaderException ex)
        {
            throw new TestDataHydrationRefusal($"table '{entry.TableName}': {ex.Message}");
        }

        // Cross-check, not decoration: `tables` and `describe` resolve the AL identity through
        // different paths, and a disagreement means one of them matched a different physical
        // table than the row count came from.
        if (schema.AlTableId != entry.AlTableId)
            throw new TestDataHydrationRefusal(
                $"table '{entry.TableName}': `tables` reports AL id {entry.AlTableId} but `describe` "
                + $"reports {schema.AlTableId}; refusing rather than hydrating the wrong table");

        var fieldIdByColumn = new Dictionary<string, int>(StringComparer.Ordinal);
        var columnByFieldId = new Dictionary<int, string>();
        foreach (var col in schema.Columns)
        {
            if (col.IsSystemColumn) continue;            // declared exclusion — see the mechanism's header
            if (col.SqlColumn == "-") continue;           // FlowField / not stored
            fieldIdByColumn[col.AlName] = col.AlFieldId!.Value;
            columnByFieldId[col.AlFieldId!.Value] = col.AlName;
        }

        var readArgs = SymbolArgs(
            new[] { "read", backup, "--table", entry.TableName, "--company", company, "--format", "json" }, symbols);
        var json = BackupReaderTool.Run(readArgs);

        List<IReadOnlyDictionary<int, JsonElement>> rows;
        try { rows = ParseRows(json, fieldIdByColumn); }
        catch (JsonException ex)
        {
            throw new TestDataHydrationRefusal(
                $"table '{entry.TableName}': the reader's JSON could not be parsed ({ex.Message})");
        }

        return RecordPatches.HydrateTestDataTable(
            entry.AlTableId.Value, entry.TableName, rows, columnByFieldId);
    }

    internal static List<IReadOnlyDictionary<int, JsonElement>> ParseRows(
        string json, IReadOnlyDictionary<string, int> fieldIdByColumn)
    {
        using var doc = JsonDocument.Parse(json);
        var rows = new List<IReadOnlyDictionary<int, JsonElement>>();
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            var row = new Dictionary<int, JsonElement>();
            foreach (var prop in element.EnumerateObject())
                if (fieldIdByColumn.TryGetValue(prop.Name, out var fieldId))
                    row[fieldId] = prop.Value.Clone();
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

    private static string ResolveCompany(string backup)
    {
        if (TestDataOptions.CompanyOverride != null) return TestDataOptions.CompanyOverride;
        var companies = BackupCatalog.ParseCompanies(BackupReaderTool.Run(new[] { "companies", backup }));
        if (companies.Count == 0)
            throw new TestDataUnavailableException(
                $"--test-data: the backup '{backup}' reports no companies, so there is nothing to hydrate.");
        return companies[0];
    }

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
