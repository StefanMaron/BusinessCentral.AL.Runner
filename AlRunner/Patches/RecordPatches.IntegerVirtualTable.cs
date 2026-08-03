// RecordPatches.IntegerVirtualTable — managed provider for the Integer system
// virtual table (2000000026).
//
// WHY THIS EXISTS
//   On the real service tier Integer is a VIRTUAL table whose rows are computed on
//   demand by Microsoft.Dynamics.Nav.Runtime.IntegerDataProvider (a
//   RangeBasedComputedDataProvider): one row per value of Number across the signed
//   integer range. There are no stored rows, and nothing is materialised until a
//   filter bounds the request.
//
//   Our runtime routes every table's data access through
//   NavDataAccessSource_GetDataAccessForTable → an in-memory TempTableDataProvider,
//   and for 2000000026 that store was empty. So `Record Integer` was ALWAYS empty.
//
//   That is not an exotic surface. `dataitem(Name; Integer)` with a
//   DataItemTableView filter is THE standard idiom for a synthetic report dataset —
//   28 of Pageworks' 29 test reports use it. With zero rows the data item body never
//   executes, so Report.SaveAs completes successfully having written nothing and
//   raised nothing. Every `asserterror Report.SaveAs(...)` around such a report then
//   fails with "An error was expected inside an ASSERTERROR statement": the runner
//   returning a silent wrong answer where real BC would render or throw.
//
// WHAT THIS DOES (faithful, managed, R2R-safe)
//   We keep the in-memory TempTableDataProvider (so BC's own filter/sort/Find engine
//   applies the AL filters exactly as it does for every other table) and POPULATE it
//   with one row per Number in a bounded window. Row values are laid out exactly as
//   BC lays out a virtual record: VirtualDataProvider.GetSystemPopulatedVirtualRecordValues
//   — BC's OWN helper — fills the timestamp / SystemId / audit slots, we write Number
//   into the slot BC's own NCLMetaField.FieldIndex says it occupies, and every other
//   column gets BC's own NavValue.GetDefaultNavValue. The Number field number is read
//   from the metatable at runtime, never hardcoded.
//
// THE WINDOW, AND WHY IT IS NOT A SILENT TRUNCATION
//   The real table is unbounded, so it cannot be materialised. We materialise
//   [IntegerWindowMin .. IntegerWindowMax] and — this is the load-bearing part —
//   a request that reaches past the window THROWS RunnerOutOfScopeException naming
//   the requested bound and the window (see IntegerWindowGuard below). Answering a
//   larger request with fewer rows would reproduce, one level up, the exact silent
//   wrong answer this file exists to remove.
//
// PRECOMPILED-DLL RESPECT
//   No BC business-logic body is touched. VirtualDataProvider, NCLMetaTable, NavValue,
//   ReadOnlyRecordBuffer and TempTableDataProvider are runtime-engine types; we call
//   BC's own helpers by reflection and feed the result into our own in-memory store.
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    internal const int IntegerVirtualTableId = 2000000026;

    /// <summary>
    /// Materialised window of Number. Real BC spans the signed integer range; we cannot.
    /// Chosen to cover every realistic synthetic-dataset use (report row generators,
    /// loop drivers) with headroom, while staying cheap enough to insert eagerly.
    /// A request beyond the window throws rather than returning a short answer.
    /// Override with AL_RUNNER_INTEGER_WINDOW_MAX for a one-off larger run.
    /// </summary>
    internal const int IntegerWindowMin = -1000;
    internal const int IntegerWindowMaxDefault = 100000;

    private static int? _ivtWindowMax;

    internal static int IntegerWindowMax
    {
        get
        {
            if (_ivtWindowMax.HasValue) return _ivtWindowMax.Value;
            var raw = Environment.GetEnvironmentVariable("AL_RUNNER_INTEGER_WINDOW_MAX");
            _ivtWindowMax = int.TryParse(raw, out var v) && v > 0 ? v : IntegerWindowMaxDefault;
            return _ivtWindowMax.Value;
        }
    }

    private static bool _ivtReflectionReady;
    private static SystemPopulatedValues? _ivtSystemValues;
    private static ConstructorInfo? _ivtCtorReadOnlyBuffer;
    private static ConstructorInfo? _ivtCtorMutableBuffer;
    private static MethodInfo? _ivtTtdpInsert;
    private static object? _ivtInsertOptionsNone;
    private static MethodInfo? _ivtNavIntegerCreate;
    private static MethodInfo? _ivtGetDefaultNavValue;

    // Number's AL field number, read off the metatable itself (never hardcoded).
    private static int? _ivtNumberFieldNo;

    // Populated-once guard per in-memory provider: the window is fixed, so unlike
    // AllObj there is nothing to top up on later handouts.
    private static readonly ConditionalWeakTable<object, object> _ivtPopulatedProviders = new();

    /// <summary>True if <paramref name="table"/> is the Integer system virtual table (2000000026).</summary>
    private static bool IsIntegerVirtualTable(NCLMetaTable? table)
        => table != null && table.TableId == IntegerVirtualTableId;

    /// <summary>
    /// Populate the in-memory store behind the Integer (2000000026) data access with one
    /// row per Number in the materialised window. Idempotent per provider.
    /// </summary>
    private static void PopulateIntegerVirtualTable(object dataAccess, NCLMetaTable integerMetaTable)
    {
        EnsureIntegerReflection(integerMetaTable);
        EnsureDataAccessProviderReflection(dataAccess);

        var provider = _pDataAccessDataProvider!.GetValue(dataAccess)
            ?? throw new RunnerOutOfScopeException(
                "Integer (virtual table 2000000026)",
                "integer-virtual-table — Integer data access has no in-memory provider; see docs/scope.md");

        // Fixed window ⇒ populate exactly once per provider.
        if (_ivtPopulatedProviders.TryGetValue(provider, out _)) return;

        // Same rationale as the Field table: make our metatable report IsVirtualTable=false
        // so BC's find takes the NORMAL temp-table DataAccess path over our populated store.
        ClearVirtualBit(integerMetaTable);

        var numberFieldNo = EnsureIntegerNumberFieldNo(integerMetaTable);
        for (int n = IntegerWindowMin; n <= IntegerWindowMax; n++)
            InsertIntegerRow(provider, integerMetaTable, numberFieldNo, n);

        _ivtPopulatedProviders.Add(provider, new object());
    }

    /// <summary>
    /// Build one Integer row and Insert it into the in-memory provider. Layout mirrors
    /// what BC produces for a virtual record: BC's own GetSystemPopulatedVirtualRecordValues
    /// fills the system slots, Number goes at its own FieldIndex, everything else gets
    /// BC's own default for that field's type.
    /// </summary>
    private static void InsertIntegerRow(object provider, NCLMetaTable integerMetaTable, int numberFieldNo, int number)
    {
        var values = _ivtSystemValues!.Invoke(integerMetaTable, IntegerVirtualTableId, number, 0, 0);

        foreach (var field in GetAllFields(integerMetaTable) ?? Enumerable.Empty<NCLMetaField>())
        {
            var idx = field.FieldIndex;
            if (idx < 0 || idx >= values.Length) continue;
            // Leave the slots BC's own helper already filled (timestamp, SystemId, audit).
            if (values.GetValue(idx) != null) continue;

            object? v = field.FieldNo == numberFieldNo
                ? _ivtNavIntegerCreate!.Invoke(null, new object?[] { number })
                : _ivtGetDefaultNavValue!.Invoke(null, new object?[] { field, false });
            values.SetValue(v, idx);
        }

        var readOnly = _ivtCtorReadOnlyBuffer!.Invoke(new object?[] { integerMetaTable, values });
        var mutable = _ivtCtorMutableBuffer!.Invoke(new object?[] { readOnly });
        try
        {
            _ivtTtdpInsert!.Invoke(provider, new object?[] { 0, mutable, _ivtInsertOptionsNone, null });
        }
        catch (TargetInvocationException tie) when (
            tie.InnerException?.GetType().Name == "NavRecordAlreadyExistsException")
        {
            // Number is unique; a repeat means this provider was already populated.
        }
    }

    /// <summary>
    /// The AL field number of Integer's "Number" column, read off the metatable's own
    /// fields. Never hardcoded: if BC's metadata shape changes we say so rather than
    /// guessing an ordinal and silently writing the value into the wrong slot.
    /// </summary>
    private static int EnsureIntegerNumberFieldNo(NCLMetaTable integerMetaTable)
    {
        if (_ivtNumberFieldNo.HasValue) return _ivtNumberFieldNo.Value;

        var allFields = GetAllFields(integerMetaTable);
        var numberField = (allFields ?? Enumerable.Empty<NCLMetaField>())
            .FirstOrDefault(f => string.Equals(f.FieldName, "Number", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"Integer metatable (2000000026) has no \"Number\" field "
                + $"[fields={(allFields == null ? "null" : string.Join("/", allFields.Select(f => $"{f.FieldNo}:{f.FieldName}")))}] "
                + "— BC metadata shape changed");

        _ivtNumberFieldNo = numberField.FieldNo;
        return _ivtNumberFieldNo.Value;
    }

    /// <summary>
    /// Bind the row-building reflection lazily off the metatable instance's own assembly,
    /// with a hard throw when a member is genuinely absent. Deliberately NOT a
    /// `?.GetValue()` chain over statics shared with another code path — that shape
    /// previously turned a runner wiring fault into a false out-of-scope claim about BC.
    /// </summary>
    private static void EnsureIntegerReflection(NCLMetaTable integerMetaTable)
    {
        if (_ivtReflectionReady) return;

        var nclAsm = integerMetaTable.GetType().Assembly;
        const string rt = "Microsoft.Dynamics.Nav.Runtime.";

        Type Need(string name) => nclAsm.GetType(name)
            ?? throw new InvalidOperationException($"{name} not found in Ncl — BC metadata shape changed");

        var tReadOnlyBuffer = Need(rt + "ReadOnlyRecordBuffer");
        var tMutableBuffer = Need(rt + "MutableRecordBuffer");
        var tTempTableProvider = Need(rt + "TempTableDataProvider");
        // NavValue/NavInteger live in Microsoft.Dynamics.Nav.Types in some builds and in
        // Ncl in others — resolve across both, exactly as the AllObj provider does.
        var tNavInteger = ResolveType(rt + "NavInteger", "Microsoft.Dynamics.Nav.Types.NavInteger")
            ?? throw new InvalidOperationException("NavInteger type not found — BC metadata shape changed");
        var tNavValue = ResolveType(rt + "NavValue", "Microsoft.Dynamics.Nav.Types.NavValue")
            ?? throw new InvalidOperationException("NavValue type not found — BC metadata shape changed");
        var tNavValueMetadata = Need(rt + "INavValueMetadata");
        var tInsertOptions = Need(rt + "InsertOptions");

        // Overload-resolved across BC versions; see SystemPopulatedValues.
        _ivtSystemValues = SystemPopulatedValues.Bind(nclAsm);

        _ivtCtorReadOnlyBuffer = tReadOnlyBuffer.GetConstructors(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(c => c.GetParameters().Length == 2)
            ?? throw new InvalidOperationException("ReadOnlyRecordBuffer(.,.) not found — BC metadata shape changed");

        _ivtCtorMutableBuffer = tMutableBuffer.GetConstructors(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(c => c.GetParameters().Length == 1
                && tReadOnlyBuffer.IsAssignableFrom(c.GetParameters()[0].ParameterType))
            ?? throw new InvalidOperationException("MutableRecordBuffer(ReadOnlyRecordBuffer) not found — BC metadata shape changed");

        _ivtTtdpInsert = tTempTableProvider.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "Insert" && m.GetParameters().Length == 4)
            ?? throw new InvalidOperationException("TempTableDataProvider.Insert(4 args) not found — BC metadata shape changed");

        _ivtInsertOptionsNone = Enum.ToObject(tInsertOptions, 0);

        // Overload-resolved by hand: NavInteger has several Create overloads and the
        // binder reports an ambiguous match for the (int) form.
        _ivtNavIntegerCreate = tNavInteger.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "Create"
                && m.GetParameters().Length == 1
                && m.GetParameters()[0].ParameterType == typeof(int))
            ?? throw new InvalidOperationException("NavInteger.Create(int) not found — BC metadata shape changed");

        _ivtGetDefaultNavValue = tNavValue.GetMethod("GetDefaultNavValue",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
            binder: null, types: new[] { tNavValueMetadata, typeof(bool) }, modifiers: null)
            ?? throw new InvalidOperationException(
                "NavValue.GetDefaultNavValue(INavValueMetadata,bool) not found — BC metadata shape changed");

        _ivtReflectionReady = true;
    }
}
