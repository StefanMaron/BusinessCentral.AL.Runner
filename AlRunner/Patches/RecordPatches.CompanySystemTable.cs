// RecordPatches.CompanySystemTable — put the runner's own company in the Company table.
//
// WHY THIS EXISTS (AlRunner#2329)
//   BcRuntime builds a skeleton NavCompany and seeds its companyName, companyNameToken and
//   companyTableId, which is what makes AL's CompanyName() answer and what stops
//   ALCompanyProperty.ALId reaching for a record. What it does NOT do is put a matching ROW
//   in the Company system table (2000000006), because that table is ordinary storage rather
//   than session state.
//
//   In real BC nothing seeds that row either — company CREATION does, at the platform level,
//   before any AL runs. The runner has no company-creation step, so the table stayed empty
//   and `Company.Get(CompanyName())` raised NavCSideRecordNotFoundException for a company
//   that, as far as every other surface was concerned, existed.
//
//   That is not an exotic read. Codeunit 9178 "Application Area Mgmt" does it in
//   SaveExperienceTierCurrentCompany, on the branch it takes when the requested experience
//   tier is already the current one — so corpus codeunit 60700's first test passed (nothing
//   was current yet) and the three that re-save an already-current tier did not. All four
//   pass on real BC on every minor from 27.0 to 28.4.
//
// WHAT THIS DOES
//   Inserts exactly one row, whose values are the ones BcRuntime already seeded onto the
//   skeleton NavCompany, so the table and the session cannot disagree:
//
//     Name               ← NavCompany.companyName        (what AL's CompanyName() returns)
//     Display Name       ← the same name
//     Evaluation Company ← false
//     Id                 ← NavCompany.companyTableId     (what ALCompanyProperty.ALId returns)
//
//   Every other column gets BC's own NavValue.GetDefaultNavValue, and every field is located
//   by NAME off the metatable at runtime — never by a hardcoded ordinal, so a BC metadata
//   change says so instead of silently writing a value into the wrong slot. Since #3015 a name
//   that does not resolve says so too: only "Name" used to be checked, and a renamed
//   "Display Name" or "Id" kept its default on a row that still inserted and still Get()s.
//
//   It runs once per bundle, immediately before CaptureInstallBaseline(), so the row is part
//   of the committed baseline every test is restored to and survives the per-codeunit restore.
//   That ordering is the whole point: seeded after the baseline, the first codeunit boundary
//   would drop it again.
//
// PRECOMPILED-DLL RESPECT
//   No BC business-logic body is touched. NCLMetaTable, NavValue, ReadOnlyRecordBuffer and
//   the temp data provider are runtime-engine types; the row is built with BC's own default
//   helper and inserted through BC's own provider Insert, exactly as the test-data hydration
//   path does for an ordinary table.
using System.Reflection;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    internal const int CompanySystemTableId = 2000000006;

    /// <summary>
    /// Which exit <see cref="EnsureCompanySystemTableRowSeeded"/> reached (AlRunner#3187). The
    /// production call site ignores it; the point of naming the exits is that
    /// <see cref="CompanySeedIsSettled"/> can decide the once-per-bundle latch PER EXIT, which
    /// a single bool set at the top of the method could not.
    /// </summary>
    internal enum CompanyRowSeedOutcome
    {
        /// <summary>A settled outcome was already reached for this bundle; nothing ran.</summary>
        AlreadySeededThisBundle,
        /// <summary>No Company metatable in this bundle's closure — nothing to seed, ever.</summary>
        NoCompanyTable,
        /// <summary>The skeleton session has no DataAccessSource YET. Reported, and retried.</summary>
        NoDataAccessSource,
        /// <summary>The skeleton NavCompany exposes no company name YET. Reported, and retried.</summary>
        NoCompanyIdentity,
        /// <summary>The row was written by this call.</summary>
        Inserted,
        /// <summary>The row was already there (BC's own already-exists refusal).</summary>
        AlreadyPresent,
        /// <summary>The seed threw. Reported at <c>[warn]</c>, NOT latched, retried.</summary>
        Failed,
    }

    private static CompanyRowSeedOutcome? _companySeedOutcomeForThisBundle;
    private static bool _companyRowSeedInProgress;

    /// <summary>The last outcome this bundle reached, or null before the first call.</summary>
    internal static CompanyRowSeedOutcome? CompanySeedOutcomeForThisBundle
        => _companySeedOutcomeForThisBundle;

    /// <summary>
    /// Whether an outcome settles the question for this bundle, so a later call may return
    /// early. #3187: only the three outcomes that answer "the row is there, or there is no row
    /// to write in this bundle at all" settle it. A report and a throw are NOT-YET answers —
    /// latching either one makes the flag mean "someone started" rather than "the row is
    /// there", which is the silent-wrong-answer .claude/rules/loud-failures.md forbids.
    /// </summary>
    internal static bool CompanySeedIsSettled(CompanyRowSeedOutcome outcome)
        => outcome is CompanyRowSeedOutcome.Inserted
                   or CompanyRowSeedOutcome.AlreadyPresent
                   or CompanyRowSeedOutcome.NoCompanyTable;

    internal static void ResetCompanySystemTableForNewBundle()
        => _companySeedOutcomeForThisBundle = null;

    /// <summary>
    /// Insert the runner's own company into the Company system table (2000000006), once per
    /// bundle. Call AFTER install triggers and company initialization and BEFORE
    /// <c>CaptureInstallBaseline()</c>, so the row is part of the restored baseline.
    /// </summary>
    internal static CompanyRowSeedOutcome EnsureCompanySystemTableRowSeeded()
        => EnsureCompanySystemTableRowSeededCore(
            resolveMeta: () => EnsureTableInMetadataCache(CompanySystemTableId),
            resolveSource: ResolveSkeletonDataAccessSource,
            readIdentity: ReadSkeletonCompanyIdentity,
            insertRow: (meta, source, name, id) =>
                InsertCompanyRow((NCLMetaTable)meta, source, name, id));

    /// <summary>
    /// The seed with its four BC-typed steps handed in, so the once-per-bundle policy above can
    /// be driven by a test: <c>NCLMetaTable</c>, the DataAccessSource and BC's own provider
    /// Insert cannot be constructed in a unit test, and cannot be made to throw in one either.
    /// The steps are the seam — there is no environment variable, and nothing here behaves
    /// differently in production. See CompanySystemTableSeedLatchTests.
    /// </summary>
    internal static CompanyRowSeedOutcome EnsureCompanySystemTableRowSeededCore(
        Func<object?> resolveMeta,
        Func<object?> resolveSource,
        Func<(string? Name, object? Id)> readIdentity,
        Action<object, object, string, object?> insertRow)
    {
        if (_companySeedOutcomeForThisBundle is { } previous && CompanySeedIsSettled(previous))
            return CompanyRowSeedOutcome.AlreadySeededThisBundle;
        // The latch is no longer set before the work, so a re-entrant call would now run the
        // seed a second time inside itself. Nothing reaches it today — this row goes straight
        // to the in-memory provider and runs no AL — but the sibling User seeder closed the
        // same window explicitly when it made this move (#2941), and the cost is one bool.
        if (_companyRowSeedInProgress) return CompanyRowSeedOutcome.AlreadySeededThisBundle;
        _companyRowSeedInProgress = true;
        try
        {
            var outcome = SeedCompanyRowCore(resolveMeta, resolveSource, readIdentity, insertRow);
            _companySeedOutcomeForThisBundle = outcome;
            return outcome;
        }
        finally
        {
            _companyRowSeedInProgress = false;
        }
    }

    private static CompanyRowSeedOutcome SeedCompanyRowCore(
        Func<object?> resolveMeta,
        Func<object?> resolveSource,
        Func<(string? Name, object? Id)> readIdentity,
        Action<object, object, string, object?> insertRow)
    {
        var meta = resolveMeta();
        if (meta == null)
            // A bundle with no Company metatable has no company concept to seed — the same
            // shape as CompanyInitializer's "no Base App in this bundle" early return. SETTLED:
            // a closure does not gain a metatable part-way through its own bundle, so a retry
            // could only re-answer the same question.
            return CompanyRowSeedOutcome.NoCompanyTable;

        var source = resolveSource();
        if (source == null)
        {
            // #3068: `[warn]`, not `[CompanySystemTable]` — Log.cs suppresses component tags at
            // default verbosity, which made "loud, never silent" untrue here for as long as this
            // carried one. Same for the two sibling branches below.
            // Loud, never silent: without this row Company.Get(CompanyName()) fails for a
            // company every other surface reports as existing, and the failure surfaces
            // several layers up inside Base App code where it reads as a corpus bug.
            Console.Error.WriteLine(
                "[warn] CompanySystemTable: the skeleton session has no DataAccessSource yet, so the "
                + "Company row (2000000006) was not seeded — Company.Get(CompanyName()) will fail. "
                + "See AlRunner#2329.");
            // NOT settled (#3187): "yet" is the whole content of this branch.
            return CompanyRowSeedOutcome.NoDataAccessSource;
        }

        var (companyName, companyId) = readIdentity();
        if (companyName == null)
        {
            Console.Error.WriteLine(
                "[warn] CompanySystemTable: the skeleton NavCompany exposes no company name, so the "
                + "Company row (2000000006) was not seeded — Company.Get(CompanyName()) will fail. "
                + "See AlRunner#2329.");
            return CompanyRowSeedOutcome.NoCompanyIdentity;
        }

        try
        {
            insertRow(meta, source, companyName, companyId);
            PerfTrace.Log($"CompanySystemTable: seeded Company row '{companyName}'");
            return CompanyRowSeedOutcome.Inserted;
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
            if (inner.GetType().Name == "NavRecordAlreadyExistsException")
            {
                PerfTrace.Log($"CompanySystemTable: Company row '{companyName}' was already present");
                return CompanyRowSeedOutcome.AlreadyPresent; // already present — not a failure.
            }
            // #3187: reported, and NOT settled. Reported rather than rethrown because this
            // seeder's caller makes that choice deliberately — the row is seeded per app group
            // OUTSIDE the persisted install-baseline snapshot, so this line fires on every run
            // rather than once on the run that poisons a cache, and a missing row takes the
            // tests that read it RED by itself. The argument is stated in full in
            // SeededRowColumns.cs's header ("WHY IT THROWS RATHER THAN REPORTING"); what #3187
            // changes is only that a run which reported this may try again.
            Console.Error.WriteLine(
                $"[warn] CompanySystemTable: could not seed the Company row (2000000006): "
                + $"{inner.GetType().Name}: {inner.Message} — Company.Get(CompanyName()) will fail. "
                + "See AlRunner#2329.");
            return CompanyRowSeedOutcome.Failed;
        }
    }

    /// <summary>
    /// The company identity BcRuntime already seeded onto the skeleton NavCompany. Read back
    /// rather than recomputed, so the row and the session are the same company by construction.
    /// </summary>
    private static (string? Name, object? Id) ReadSkeletonCompanyIdentity()
    {
        const BindingFlags F = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public;

        var session = AlRunner.BcRuntime.SkeletonSession;
        // The session's Company getter is the same one every BC caller goes through, so the
        // NavCompany read here is by construction the one BcRuntime seeded.
        var company = session?.GetType().GetProperty("Company", F)?.GetValue(session);
        if (company == null) return (null, null);

        var name = company.GetType().GetField("companyName", F)?.GetValue(company) as string;
        var id = company.GetType().GetField("companyTableId", F)?.GetValue(company);
        return (name, id);
    }

    private static void InsertCompanyRow(NCLMetaTable meta, object source, string companyName, object? companyId)
    {
        var fieldByName = new Dictionary<string, NCLMetaField>(StringComparer.OrdinalIgnoreCase);
        for (var fi = 0; fi < meta.FieldCount; fi++)
        {
            var f = meta.GetFieldByIndex(fi);
            fieldByName[f.FieldName] = f;
        }

        var values = new NavValue[meta.FieldCount];
        for (var fi = 0; fi < meta.FieldCount; fi++)
        {
            var f = meta.GetFieldByIndex(fi);
            var idx = f.FieldIndex;
            if (idx < 0 || idx >= values.Length) continue;
            // NCLMetaField.EmptyValue is BC's own per-field default (it routes to
            // NavValue.GetDefaultNavValue), so no column is left null and none is invented.
            values[idx] = f.EmptyValue;
        }

        // #3015 — this used to hard-check only "Name" and skip every other column in silence,
        // so a renamed "Display Name" or "Id" produced a row that was inserted, found by its
        // primary key, and wrong. Each Set() below now declares its column required; one that
        // does not resolve is raised before the Insert.
        //
        // WHERE THAT RAISE GOES, and why it is not the refusal the Published Application
        // seeder makes. Two reasons, and the second is the stronger one:
        //
        //   1. This row is seeded per app group, AFTER the dep+company baseline cache block in
        //      TestExecutor (line 606 — the block ends at 594), so it is NOT part of the
        //      snapshot persisted to disk. The caller's existing catch therefore reaches stderr
        //      on EVERY run, rather than once on the single run that would have poisoned a
        //      cache and then never again.
        //
        //   2. Stderr is not the only signal, and it is not the one that stops the run. A
        //      refused row is a row that was never inserted, so Company.Get(CompanyName())
        //      raises for a company every other surface reports as existing and the tests that
        //      read it go RED — which is how #2329 presented in the first place. That is worth
        //      naming, because Console.Error on its own has repeatedly not been a sufficient
        //      signal in this repository.
        //
        // The dependency Published Application rows are inside the persisted MISS branch and
        // have neither property, so they refuse.
        var columns = new SeededRowColumns<NCLMetaField>(
            $"{meta.TableName} (system table {CompanySystemTableId})", fieldByName,
            slotOf: f => f.FieldIndex,
            describeField: f => $"{f.FieldNo}:{f.FieldName}",
            slotCount: values.Length);

        // BC's own value factory, so each column is typed by its own field metadata rather
        // than by a type this file picked.
        void Set(string fieldName, object? value)
        {
            // Resolve FIRST, then look at the value: companyId is genuinely nullable (it is
            // read by reflection off the skeleton NavCompany), and checking the value first
            // would let a renamed "Id" hide behind a run where there was nothing to write.
            if (!columns.TryResolve(fieldName, out var f, out var idx)) return;
            // A null is the absence of a VALUE, not a metadata mismatch: BC's own EmptyValue,
            // already in the slot, is the faithful answer.
            if (value == null) return;
            values[idx] = value is NavValue already
                ? already
                : NavValue.CreateNavValueFromObject(f, value);
        }

        Set("Name", companyName);
        Set("Display Name", companyName);
        Set("Evaluation Company", false);
        Set("Id", companyId);
        columns.ThrowIfAnyColumnCouldNotBeWritten();

        var perTable = _dataAccessByTable.GetValue(source,
            static _ => new System.Collections.Concurrent.ConcurrentDictionary<int, object>());
        var dataAccess = perTable.GetOrAdd(CompanySystemTableId,
            _ => _mCreateTempDataAccess!.Invoke(source, new object[] { meta })!);
        var provider = GetDataProvider(dataAccess)
            ?? throw new InvalidOperationException(
                $"Company ({CompanySystemTableId}) data access exposes no in-memory DataProvider");

        var insert = provider.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .First(m => m.Name == "Insert" && m.GetParameters().Length == 4
                     && m.GetParameters()[0].ParameterType == typeof(int));
        var insertOptions = Enum.ToObject(insert.GetParameters()[2].ParameterType, 0);

        var mutableCtor = typeof(ReadOnlyRecordBuffer).Assembly
            .GetType("Microsoft.Dynamics.Nav.Runtime.MutableRecordBuffer")
            ?.GetConstructor(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                binder: null, types: new[] { typeof(ReadOnlyRecordBuffer) }, modifiers: null)
            ?? throw new InvalidOperationException(
                "MutableRecordBuffer(ReadOnlyRecordBuffer) not found — BC metadata shape changed");

        var readOnly = new ReadOnlyRecordBuffer(meta, values);
        var mutable = mutableCtor.Invoke(new object[] { readOnly });
        insert.Invoke(provider, new object?[] { 0, mutable, insertOptions, null });
    }
}
