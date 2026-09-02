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
//   change says so instead of silently writing a value into the wrong slot.
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

    private static bool _companyRowSeededForThisBundle;

    internal static void ResetCompanySystemTableForNewBundle() => _companyRowSeededForThisBundle = false;

    /// <summary>
    /// Insert the runner's own company into the Company system table (2000000006), once per
    /// bundle. Call AFTER install triggers and company initialization and BEFORE
    /// <c>CaptureInstallBaseline()</c>, so the row is part of the restored baseline.
    /// </summary>
    internal static void EnsureCompanySystemTableRowSeeded()
    {
        if (_companyRowSeededForThisBundle) return;
        _companyRowSeededForThisBundle = true;

        var meta = EnsureTableInMetadataCache(CompanySystemTableId);
        if (meta == null)
            // A bundle with no Company metatable has no company concept to seed — the same
            // shape as CompanyInitializer's "no Base App in this bundle" early return.
            return;

        var source = ResolveSkeletonDataAccessSource();
        if (source == null)
        {
            // Loud, never silent: without this row Company.Get(CompanyName()) fails for a
            // company every other surface reports as existing, and the failure surfaces
            // several layers up inside Base App code where it reads as a corpus bug.
            Console.Error.WriteLine(
                "[CompanySystemTable] the skeleton session has no DataAccessSource yet, so the "
                + "Company row (2000000006) was not seeded — Company.Get(CompanyName()) will fail. "
                + "See AlRunner#2329.");
            return;
        }

        var (companyName, companyId) = ReadSkeletonCompanyIdentity();
        if (companyName == null)
        {
            Console.Error.WriteLine(
                "[CompanySystemTable] the skeleton NavCompany exposes no company name, so the "
                + "Company row (2000000006) was not seeded — Company.Get(CompanyName()) will fail. "
                + "See AlRunner#2329.");
            return;
        }

        try
        {
            InsertCompanyRow(meta, source, companyName, companyId);
            PerfTrace.Log($"CompanySystemTable: seeded Company row '{companyName}'");
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
            if (inner.GetType().Name == "NavRecordAlreadyExistsException")
                return; // already present — nothing to do, and not a failure.
            Console.Error.WriteLine(
                $"[CompanySystemTable] could not seed the Company row (2000000006): "
                + $"{inner.GetType().Name}: {inner.Message} — Company.Get(CompanyName()) will fail. "
                + "See AlRunner#2329.");
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

        // BC's own value factory, so each column is typed by its own field metadata rather
        // than by a type this file picked. A field the metatable does not have is skipped:
        // "Name" is the only one whose absence is a shape change worth failing on.
        void Set(string fieldName, object? value)
        {
            if (value == null) return;
            if (!fieldByName.TryGetValue(fieldName, out var f)) return;
            var idx = f.FieldIndex;
            if (idx < 0 || idx >= values.Length) return;
            values[idx] = value is NavValue already
                ? already
                : NavValue.CreateNavValueFromObject(f, value);
        }

        // "Name" is the primary key and the value AL's CompanyName() returns, so it is the
        // one field whose absence means this is not the Company table we think it is.
        if (!fieldByName.ContainsKey("Name"))
            throw new InvalidOperationException(
                $"Company metatable ({CompanySystemTableId}) has no \"Name\" field "
                + $"[fields={string.Join("/", fieldByName.Values.Select(f => $"{f.FieldNo}:{f.FieldName}"))}] "
                + "— BC metadata shape changed");

        Set("Name", companyName);
        Set("Display Name", companyName);
        Set("Evaluation Company", false);
        Set("Id", companyId);

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
