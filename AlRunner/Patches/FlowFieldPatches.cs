// FlowFieldPatches — implements FlowField evaluation (Sum/Count/Average/Min/Max/Exist/Lookup)
// directly against the in-memory TempTableDataProvider store, bypassing the broken async
// FlowFieldsHelper pipeline.
//
// Strategy:
//   The decompiled BC code path is
//     NavRecord.CalcFieldsAsync(DataError,int[])  [async ValueTask<bool>]
//       → recordImplementation.CalcFieldsAsync(DataError, NCLMetaField[])  [sync wrapper]
//          → CalcFieldsAsync(DataError, NCLMetaField[], bool)              [private async]
//             → FlowFieldsHelper.CalcFieldsAsync(...)                       [NREs on skeleton]
//
//   The async pipeline NREs on Session.Company.CompanyNameToken and friends. We hook BOTH
//   the 2-arg sync wrapper (called directly by NavRecord.CalcFieldsAsync) AND the 3-arg
//   private async overload (called directly by CalcAutoCalcFieldsAsync), so neither path
//   reaches FlowFieldsHelper.
//
//   #1757 adds a THIRD entry point: FlowFieldsHelper.CalcFieldsAsync itself (the 9-arg
//   static). BC re-enters that one from inside its own code — GetFilterFromMetaFilterCollection
//   resolves a `where(X = field("<a FlowField>"))` condition by recursively calculating the
//   referenced FlowField, and RecordIsWithinFilteredFlowFieldsAsync calls it too — so the two
//   record-level hooks above never see those calls. All three entry points now run the same
//   CalcFlowFieldValuesCore, which also reproduces BC's two recursion guards (recursionLevel
//   > 50 and FieldsAndFormulaAreSelfReferencing → NavNCLStackOverflowException) so a cyclic
//   CalcFormula fails the way BC fails it instead of overflowing the native stack.
//
//   The replacement reads each FlowField's NCLMetaCalculationFormula (already populated by
//   RecordPatches.NclMetaTableBuilder.BuildMetaCalcFormula), enumerates the source table
//   in-memory via TempTableDataProvider.Filter (the same path used by the existing
//   TempTableDataProvider_CalcNumeric replacement), applies the formula's NCLMetaFilterField
//   filters by reading the parent's mutable buffer at filter.ValueField.ColumnIndex, and
//   aggregates per CalculationMethod. Result is written into self.mutableRecordBuffer at
//   field.ColumnIndex (== field.FieldIndex — same backing store).
//
//   JmpHook is safe on async-returning methods (the original body never runs, so no async
//   state-machine is constructed). The replacement returns ValueTask<bool> synchronously
//   via `new ValueTask<bool>(true)`.
using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;

namespace AlRunner.Patches;

public static class FlowFieldPatches
{
    // ── Reflection cache populated in Register() ─────────────────────────────
    private static FieldInfo? _fRecImplDataAccess;
    private static FieldInfo? _fRecImplMetaTable;
    private static FieldInfo? _fRecImplTableState;
    private static FieldInfo? _fRecImplMutableRecordBuffer;
    private static FieldInfo? _fRecImplSession;            // RecordImplementation.session
    private static PropertyInfo? _pRecImplCurrentBufferOrDefault;

    private static FieldInfo? _fTableStateCompanyNameToken;
    private static PropertyInfo? _pDataAccessDataProvider;     // DataAccess.DataProvider
    private static FieldInfo? _fSessionDataAccessSource;
    private static MethodInfo? _mDataAccessSourceGetDataAccessForTable;
    private static FieldInfo? _fEmptyFiltersAndMarks;          // FiltersAndMarks.Empty (internal static readonly)

    private static FieldInfo? _fTtdpPrimaryKeySortingFields;   // also held in RecordPatches but private
    private static MethodInfo? _mTtdpFilter;                   // Filter(int,FiltersAndMarks,MutableRecordBuffer,SortingFieldList,bool)
    private static MethodInfo? _mTtdpTryGetValue;              // TryGetValue(NavRecordId, out TempTableRecordBuffer)

    // Blob CalcFields helpers
    private static PropertyInfo? _pNclMetaFieldFieldNclType;   // NCLMetaField.FieldNclType
    private static object? _nclTypeNavBlob;                    // NavNclType.NavBlob
    private static MethodInfo? _mMutableBufferGetChangedFieldValue; // MutableRecordBuffer.GetChangedFieldValue(int)
    private static MethodInfo? _mMutableBufferGetOriginalValue;     // MutableRecordBuffer.GetOriginalValue(int)
    private static MethodInfo? _mMutableBufferGetRecordId;          // MutableRecordBuffer.GetRecordId()

    private static Type? _tNCLMetaField;
    private static Type? _tNCLMetaFilter;
    private static Type? _tNCLMetaFilterField;
    private static Type? _tNCLMetaCalcFormula;
    private static Type? _tNCLMetaCalcMethod;
    private static Type? _tFieldClass;
    private static PropertyInfo? _pNclMetaFieldFieldClass;
    private static PropertyInfo? _pNclMetaFieldColumnIndex;
    private static PropertyInfo? _pNclMetaFieldCalculationFormula;
    private static PropertyInfo? _pNclMetaFieldParent;
    private static PropertyInfo? _pCalcFormulaCalculationMethod;
    private static PropertyInfo? _pCalcFormulaFilters;
    private static PropertyInfo? _pNclMetaFilterFilterType;   // NCLMetaFilter.FilterType
    private static object? _filterTypeField;                  // NCLMetaFilterType.Field
    private static PropertyInfo? _pCalcFormulaSourceField;
    private static PropertyInfo? _pCalcFormulaNegateResult;
    private static MethodInfo? _mCalcFormulaNegateValue;      // NCLMetaCalculationFormula.NegateValue(NavValue)
    private static PropertyInfo? _pCalcFormulaTableId;
    private static PropertyInfo? _pCalcFormulaFieldId;
    private static PropertyInfo? _pFilterFieldValueField;      // NCLMetaFilterField.ValueField (returns INavFieldMetadata)
    private static FieldInfo? _fCalcFormulaEmpty;              // NCLMetaCalculationFormula.EmptyFormula

    private static MethodInfo? _mGetFilterFromMetaFilterCollection; // FlowFieldsHelper (#1716)

    // #2970 — BC's own per-FlowField CalcFormula validator,
    // FlowFieldsHelper.CheckFlowFieldProperties(NCLMetaField). BC reaches it from
    // DistinctSourceTable.AddField, which is the first statement of that method and runs for
    // every FlowField inside GetDistinctSourceTablesFromFlowFields — i.e. on the path this
    // patch replaces, which is why all five of its refusals went missing together. Bound as a
    // typed delegate rather than called through MethodInfo.Invoke so the NavCSideException it
    // raises reaches AL as itself, not wrapped in a TargetInvocationException.
    private static Action<NCLMetaField>? _checkFlowFieldProperties;
    private static ConstructorInfo? _ctorFieldDictionary;      // FieldDictionary<NavValue>(Tuple<INavFieldMetadata,NavValue>[]) (#1757)
    private static ConstructorInfo? _ctorFiltersAndMarks;      // FiltersAndMarks(FilterFieldDictionary)
    private static PropertyInfo? _pTableStateFiltersAndMarks;  // TableState.FiltersAndMarks
    private static PropertyInfo? _pTableStateReadIsolation;    // TableState.ReadIsolation
    private static FieldInfo? _fRecImplSecurityFiltering;      // RecordImplementation.securityFiltering

    // CalculationMethod enum values
    private static object? _cmNone, _cmSum, _cmCount, _cmAverage, _cmMin, _cmMax, _cmExist, _cmLookup;
    private static object? _fcFlowField; // FieldClass.FlowField

    // Cached singleton FiltersAndMarks.Empty
    private static object? _emptyFm;

    private static bool _registered;

    public static void Register(Assembly nclAsm, Assembly typesAsm)
    {
        if (_registered) return;
        _registered = true;

        var tRecImpl = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.RecordImplementation");
        var tNavRecord = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.NavRecord");
        var tDataError = typesAsm.GetType("Microsoft.Dynamics.Nav.Types.DataError");
        _tNCLMetaField = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.NCLMetaField");
        _tNCLMetaFilter = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.NCLMetaFilter");
        _tNCLMetaFilterField = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.NCLMetaFilterField");
        _tNCLMetaCalcFormula = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.NCLMetaCalculationFormula");
        _tNCLMetaCalcMethod = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.NCLMetaCalculationMethod");
        _tFieldClass = typesAsm.GetType("Microsoft.Dynamics.Nav.Types.Metadata.FieldClass");

        if (tRecImpl == null || tNavRecord == null || tDataError == null
            || _tNCLMetaField == null || _tNCLMetaFilter == null || _tNCLMetaFilterField == null
            || _tNCLMetaCalcFormula == null || _tNCLMetaCalcMethod == null || _tFieldClass == null)
        {
            Console.Error.WriteLine("[FlowFieldPatches] WARN: required BC types not found, FlowField hook DISABLED");
            return;
        }

        // RecordImplementation private fields
        _fRecImplDataAccess = tRecImpl.GetField("dataAccess", BindingFlags.NonPublic | BindingFlags.Instance);
        _fRecImplMetaTable = tRecImpl.GetField("metaTable", BindingFlags.NonPublic | BindingFlags.Instance);
        _fRecImplTableState = tRecImpl.GetField("tableState", BindingFlags.NonPublic | BindingFlags.Instance);
        _fRecImplMutableRecordBuffer = tRecImpl.GetField("mutableRecordBuffer", BindingFlags.NonPublic | BindingFlags.Instance);
        _pRecImplCurrentBufferOrDefault = tRecImpl.GetProperty("CurrentMutableRecordBufferOrDefault",
            BindingFlags.NonPublic | BindingFlags.Instance);
        // session lives on TreeObject base or RecordImplementation; try both
        _fRecImplSession = tRecImpl.GetField("session", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? tRecImpl.BaseType?.GetField("session", BindingFlags.NonPublic | BindingFlags.Instance);

        // TableState
        var tTableState = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.TableState");
        _fTableStateCompanyNameToken = tTableState?.GetField("CompanyNameToken",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (_fTableStateCompanyNameToken == null)
        {
            // Property-backed
            var p = tTableState?.GetProperty("CompanyNameToken",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (p != null)
                _fTableStateCompanyNameToken = tTableState!.GetField($"<{p.Name}>k__BackingField",
                    BindingFlags.NonPublic | BindingFlags.Instance);
        }

        // DataAccess.DataProvider getter
        var tDataAccess = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.DataAccess");
        _pDataAccessDataProvider = tDataAccess?.GetProperty("DataProvider",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        // NavSession.dataAccessSource (BackingField on the public DataAccessSource property)
        var tNavSession = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.NavSession");
        _fSessionDataAccessSource = tNavSession?.GetField("<DataAccessSource>k__BackingField",
            BindingFlags.NonPublic | BindingFlags.Instance);

        // DataAccessSource.GetDataAccessForTable(NCLMetaTable, bool) — already JmpHooked to in-memory route
        var tDataAccessSource = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.DataAccessSource");
        _mDataAccessSourceGetDataAccessForTable = tDataAccessSource?.GetMethod("GetDataAccessForTable",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        // FiltersAndMarks.Empty
        var tFm = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.FiltersAndMarks");
        _fEmptyFiltersAndMarks = tFm?.GetField("Empty",
            BindingFlags.NonPublic | BindingFlags.Static);
        _emptyFm = _fEmptyFiltersAndMarks?.GetValue(null);

        // TempTableDataProvider.Filter + primaryKeySortingFields + TryGetValue (for blob CalcFields)
        var tTtdp = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.TempTableDataProvider");
        _fTtdpPrimaryKeySortingFields = tTtdp?.GetField("primaryKeySortingFields",
            BindingFlags.NonPublic | BindingFlags.Instance);
        _mTtdpFilter = tTtdp?.GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "Filter" && m.GetParameters().Length == 5);
        _mTtdpTryGetValue = tTtdp?.GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "TryGetValue" && m.GetParameters().Length == 2
                && m.GetParameters()[0].ParameterType.Name == "NavRecordId");

        // Blob CalcFields: FieldNclType, NavNclType.NavBlob, MutableRecordBuffer helpers
        _pNclMetaFieldFieldNclType = _tNCLMetaField.GetProperty("FieldNclType",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var tNavNclType = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.NavNclType");
        if (tNavNclType != null)
            _nclTypeNavBlob = Enum.Parse(tNavNclType, "NavBlob");
        var tMutableBuffer = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.MutableRecordBuffer");
        _mMutableBufferGetChangedFieldValue = tMutableBuffer?.GetMethod("GetChangedFieldValue",
            BindingFlags.Public | BindingFlags.Instance);
        _mMutableBufferGetOriginalValue = tMutableBuffer?.GetMethod("GetOriginalValue",
            BindingFlags.Public | BindingFlags.Instance);
        _mMutableBufferGetRecordId = tMutableBuffer?.GetMethod("GetRecordId",
            BindingFlags.Public | BindingFlags.Instance);

        // NCLMetaField properties
        _pNclMetaFieldFieldClass = _tNCLMetaField.GetProperty("FieldClass",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        _pNclMetaFieldColumnIndex = _tNCLMetaField.GetProperty("ColumnIndex",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        _pNclMetaFieldCalculationFormula = _tNCLMetaField.GetProperty("CalculationFormula",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        _pNclMetaFieldParent = _tNCLMetaField.GetProperty("Parent",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        // NCLMetaCalculationFormula
        _pCalcFormulaCalculationMethod = _tNCLMetaCalcFormula.GetProperty("CalculationMethod");
        _pCalcFormulaFilters = _tNCLMetaCalcFormula.GetProperty("Filters");
        _pCalcFormulaSourceField = _tNCLMetaCalcFormula.GetProperty("SourceField");
        _pCalcFormulaNegateResult = _tNCLMetaCalcFormula.GetProperty("NegateResult");
        _mCalcFormulaNegateValue = _tNCLMetaCalcFormula.GetMethod("NegateValue",
            BindingFlags.Public | BindingFlags.Instance);
        _pCalcFormulaTableId = _tNCLMetaCalcFormula.GetProperty("TableId");
        _pCalcFormulaFieldId = _tNCLMetaCalcFormula.GetProperty("FieldId");
        _fCalcFormulaEmpty = _tNCLMetaCalcFormula.GetField("EmptyFormula",
            BindingFlags.Public | BindingFlags.Static);

        // NCLMetaFilter / NCLMetaFilterField
        _pFilterFieldValueField = _tNCLMetaFilterField.GetProperty("ValueField",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        _pNclMetaFilterFilterType = _tNCLMetaFilter.GetProperty("FilterType",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var tFilterTypeEnum = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.NCLMetaFilterType");
        if (tFilterTypeEnum != null)
            _filterTypeField = Enum.Parse(tFilterTypeEnum, "Field");
        // #1716 — BC's own metadata→FilterFieldDictionary resolution. Everything about a
        // where-condition (FIELD equality with type transfer, CONST, FILTER, the flow-filter
        // forms, and the ordering rule that a later ValueIsFilter condition REPLACES an
        // earlier link on the same source field instead of ANDing with it) lives in this one
        // method, so the runner calls it rather than restating any of it.
        var tFlowFieldsHelper = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.FlowFieldsHelper");
        _mGetFilterFromMetaFilterCollection = tFlowFieldsHelper?.GetMethod(
            "GetFilterFromMetaFilterCollection", BindingFlags.NonPublic | BindingFlags.Static);
        // #2970 — internal static void CheckFlowFieldProperties(NCLMetaField). Internal on a
        // runtime-engine DLL, which precompiled-dll-respect.md puts squarely on the "ours to
        // work with" side: nothing about it is rewritten, it is only called from the place BC
        // calls it from.
        var mCheckFlowFieldProperties = tFlowFieldsHelper?.GetMethod(
            "CheckFlowFieldProperties", BindingFlags.NonPublic | BindingFlags.Static,
            null, new[] { _tNCLMetaField }, null);
        if (mCheckFlowFieldProperties != null)
            _checkFlowFieldProperties = (Action<NCLMetaField>)Delegate.CreateDelegate(
                typeof(Action<NCLMetaField>), mCheckFlowFieldProperties);
        else
            Console.Error.WriteLine(
                "[FlowFieldPatches] WARN: FlowFieldsHelper.CheckFlowFieldProperties not found — "
                + "CalcFormula validation will be REFUSED rather than skipped (#2970)");
        var tFiltersAndMarks = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.FiltersAndMarks");
        var tFilterFieldDictionary = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.FilterFieldDictionary");
        if (tFiltersAndMarks != null && tFilterFieldDictionary != null)
            _ctorFiltersAndMarks = tFiltersAndMarks.GetConstructor(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, new[] { tFilterFieldDictionary }, null);
        _pTableStateFiltersAndMarks = tTableState?.GetProperty("FiltersAndMarks",
            BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
        _pTableStateReadIsolation = tTableState?.GetProperty("ReadIsolation",
            BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
        _fRecImplSecurityFiltering = tRecImpl.GetField("securityFiltering",
            BindingFlags.NonPublic | BindingFlags.Instance);
        // #1757 — FieldDictionary<NavValue> is the return shape BC's own
        // FlowFieldsHelper.CalcFieldsAsync hands back to its callers, so the replacement
        // that stands in for that method has to build a real one. The type is internal to
        // Ncl, but both its generic argument (NavValue) and its key type
        // (INavFieldMetadata) are public, so the item array is nameable here and only the
        // construction needs reflection.
        var tFieldDictionary = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.FieldDictionary`1");
        if (tFieldDictionary != null)
            _ctorFieldDictionary = tFieldDictionary.MakeGenericType(typeof(NavValue))
                .GetConstructor(new[] { typeof(Tuple<INavFieldMetadata, NavValue>[]) });
        if (_mGetFilterFromMetaFilterCollection == null || _ctorFiltersAndMarks == null
            || _pTableStateFiltersAndMarks == null)
        {
            Console.Error.WriteLine(
                "[FlowFieldPatches] WARN: FlowFieldsHelper.GetFilterFromMetaFilterCollection / "
                + "FiltersAndMarks(FilterFieldDictionary) / TableState.FiltersAndMarks not found — "
                + "CalcFormula where-conditions will be REFUSED rather than guessed at (#1716)");
        }

        // Enum values
        _cmNone   = Enum.Parse(_tNCLMetaCalcMethod, "None");
        _cmSum    = Enum.Parse(_tNCLMetaCalcMethod, "Sum");
        _cmCount  = Enum.Parse(_tNCLMetaCalcMethod, "Count");
        _cmAverage= Enum.Parse(_tNCLMetaCalcMethod, "Average");
        _cmMin    = Enum.Parse(_tNCLMetaCalcMethod, "Min");
        _cmMax    = Enum.Parse(_tNCLMetaCalcMethod, "Max");
        _cmExist  = Enum.Parse(_tNCLMetaCalcMethod, "Exists");
        _cmLookup = Enum.Parse(_tNCLMetaCalcMethod, "Lookup");
        _fcFlowField = Enum.Parse(_tFieldClass, "FlowField");

        // ── Install JmpHooks on both CalcFieldsAsync overloads on RecordImplementation ──
        var twoArg = tRecImpl.GetMethod("CalcFieldsAsync",
            BindingFlags.NonPublic | BindingFlags.Instance,
            null, new[] { tDataError, _tNCLMetaField.MakeArrayType() }, null);
        var threeArg = tRecImpl.GetMethod("CalcFieldsAsync",
            BindingFlags.NonPublic | BindingFlags.Instance,
            null, new[] { tDataError, _tNCLMetaField.MakeArrayType(), typeof(bool) }, null);

        var repl2 = typeof(FlowFieldPatches).GetMethod(nameof(RecordImpl_CalcFieldsAsync_2),
            BindingFlags.Public | BindingFlags.Static)!;
        var repl3 = typeof(FlowFieldPatches).GetMethod(nameof(RecordImpl_CalcFieldsAsync_3),
            BindingFlags.Public | BindingFlags.Static)!;

        if (twoArg != null)
        {
            if (!JmpHook.InstallIndirect(twoArg, repl2, "RecordImplementation.CalcFieldsAsync(2)"))
                JmpHook.Apply(twoArg, repl2, "RecordImplementation.CalcFieldsAsync(DataError,NCLMetaField[])");
            Console.Error.WriteLine("[FlowFieldPatches] hooked RecordImplementation.CalcFieldsAsync(2)");
        }
        else
            Console.Error.WriteLine("[FlowFieldPatches] WARN: 2-arg CalcFieldsAsync not found");

        if (threeArg != null)
        {
            if (!JmpHook.InstallIndirect(threeArg, repl3, "RecordImplementation.CalcFieldsAsync(3)"))
                JmpHook.Apply(threeArg, repl3, "RecordImplementation.CalcFieldsAsync(DataError,NCLMetaField[],bool)");
            Console.Error.WriteLine("[FlowFieldPatches] hooked RecordImplementation.CalcFieldsAsync(3)");
        }
        else
            Console.Error.WriteLine("[FlowFieldPatches] WARN: 3-arg CalcFieldsAsync not found");
    }

    // ── FlowFieldsHelper.FieldsAndFormulaAreSelfReferencing replacement ──────────
    // Null-safe equivalent of the BC body.
    //
    // The real BC body is:
    //   foreach (NCLMetaField f in fieldsToCalc)
    //     foreach (NCLMetaFilter filter in f.CalculationFormula.Filters)
    //       if (filter.FilterType == Field && Equals(((NCLMetaFilterField)filter).ValueField, f))
    //         return true;
    //   return false;
    //
    // ROOT CAUSE of the NRE (file-probe + decompile evidence):
    //   On the skeleton runtime, a FlowField metafield whose CalculationFormula could
    //   not be materialised falls back to the shared `NCLMetaCalculationFormula.EmptyFormula`
    //   singleton (NCLMetaField ctor: `metaCalculationFormula != null ? CreateFrom... : EmptyFormula`).
    //   EmptyFormula is constructed as `new NCLMetaCalculationFormula(0,0,None,false, null)` —
    //   its `Filters` (an NCLMetaFilterCollection) is NULL. The real BC body then NREs on
    //   `EmptyFormula.Filters` because EmptyFormula is never reached this way on a live tier
    //   (a live tier always has a real formula). Observed on Purchase Line "Matched Order Lines"
    //   (table 39, field 2701) during Purch.-Post → ProcessMatchedReceiptOnInvoice, when a
    //   temp Purchase Line buffer is filtered on the FlowField via TempTableDataProvider's
    //   RecordBufferEvaluatorVisitor.
    //
    // FAITHFULNESS: a formula with no field-type filters cannot be self-referencing via a
    // field filter, so the correct answer for a null/empty Filters collection is `false` —
    // identical to what BC computes when the foreach over a non-null empty collection runs
    // zero iterations. This is the runtime-engine layer (Ncl), not AL business logic; the
    // guard only hardens the self-reference probe against the skeleton's EmptyFormula.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool FieldsAndFormulaAreSelfReferencing(Array fieldsToCalc)
    {
        if (fieldsToCalc == null) throw new ArgumentNullException(nameof(fieldsToCalc));
        foreach (var fieldObj in fieldsToCalc)
        {
            if (fieldObj == null) continue;
            var formula = _pNclMetaFieldCalculationFormula!.GetValue(fieldObj);
            if (formula == null) continue;
            var filters = _pCalcFormulaFilters!.GetValue(formula);
            if (filters == null) continue;                 // EmptyFormula: null collection → no self-ref
            foreach (var filter in (System.Collections.IEnumerable)filters)
            {
                if (filter == null) continue;
                // NCLMetaFilterField carries ValueField; FilterType==Field means a field filter.
                var filterTypeObj = _pNclMetaFilterFilterType!.GetValue(filter);
                if (!Equals(filterTypeObj, _filterTypeField)) continue;
                // Only NCLMetaFilterField exposes ValueField; cast-safe via the cached property.
                if (!_tNCLMetaFilterField!.IsInstanceOfType(filter)) continue;
                var valueField = _pFilterFieldValueField!.GetValue(filter);
                if (Equals(valueField, fieldObj)) return true;
            }
        }
        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static System.Threading.Tasks.ValueTask<bool> RecordImpl_CalcFieldsAsync_2(
        object self, DataError errorLevel, Array fields)
    {
        return RecordImpl_CalcFieldsAsync_3(self, errorLevel, fields!, false);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static System.Threading.Tasks.ValueTask<bool> RecordImpl_CalcFieldsAsync_3(
        object self, DataError errorLevel, Array fields, bool onlyFieldsSourcedFromVirtualTables)
    {
        try
        {
            if (fields == null || fields.Length == 0)
                return new System.Threading.Tasks.ValueTask<bool>(true);

            // Get parent buffer; allocate default-via-property if null and assign back to field
            // (mirrors the decompiled BC pattern: `mutableRecordBuffer = CurrentMutableRecordBufferOrDefault;`).
            var parentBuffer = _fRecImplMutableRecordBuffer?.GetValue(self);
            if (parentBuffer == null)
            {
                parentBuffer = _pRecImplCurrentBufferOrDefault?.GetValue(self);
                if (parentBuffer != null)
                    _fRecImplMutableRecordBuffer?.SetValue(self, parentBuffer);
            }
            if (parentBuffer == null) return new System.Threading.Tasks.ValueTask<bool>(false);

            // Get session from NavCurrentThread.Session (RecordImplementation has no session field)
            object? session = null;
            try
            {
                var tNCT = self.GetType().Assembly.GetType("Microsoft.Dynamics.Nav.Runtime.NavCurrentThread");
                var pSess = tNCT?.GetProperty("Session", BindingFlags.Public | BindingFlags.Static);
                session = pSess?.GetValue(null);
            }
            catch { }
            if (session == null) return new System.Threading.Tasks.ValueTask<bool>(false);

            // companyToken from tableState
            int companyToken = 0;
            var tableState = _fRecImplTableState?.GetValue(self);
            if (tableState != null && _fTableStateCompanyNameToken != null)
            {
                var v = _fTableStateCompanyNameToken.GetValue(tableState);
                if (v is int i) companyToken = i;
            }

            // Buffer write helper via indexer
            var bufferType = parentBuffer.GetType();
            var bufferIndexer = bufferType.GetProperty("Item",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, typeof(NavValue), new[] { typeof(int) }, null)
                ?? bufferType.GetProperty("Item",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (bufferIndexer == null) return new System.Threading.Tasks.ValueTask<bool>(false);

            // BC's own classification of the field list, BEFORE anything is loaded or
            // calculated (#3012). This is where `Rec.CalcFields("No.")` and
            // `Rec.CalcFields("<a FlowFilter>")` are refused, and where a FlowField with no
            // CalcFormula is refused — see ClassifyCalcFieldsRequest for BC's own loop. It
            // runs first for the reason BC runs it first: a refused call must compute nothing
            // and load nothing, not leave the acceptable part of the list applied.
            var blobFields = new List<object>();
            var flowFields = new List<object>();
            ClassifyCalcFieldsRequest(fields, blobFields, flowFields);

            // BLOBs. BC excludes them from the FlowField pipeline entirely
            // (`fieldsToCalc.Where(f => f.FieldNclType != NavNclType.NavBlob)`) and loads their
            // content from the record's OWN DataAccess, so this stays on the RecordImplementation
            // entry point where `self` is available — the shared core below has no `self`.
            foreach (var fieldObj in blobFields)
            {
                // #2771: a BLOB the runner has no source for refuses HERE, by name, before
                // anything is loaded. This is the runner's own blob-load site and therefore
                // the only one AL can reach — the replacement above means BC's
                // DataAccess.GetBlobContentAsync never runs. Handing back the 0-byte
                // placeholder instead reads as a legitimately empty BLOB (HasValue() false,
                // CreateInStream empty), which is the silent default loud-failures.md forbids.
                RecordPatches.ThrowIfColumnHasNoSource(fieldObj as NCLMetaField);
                int blobColumn = -1;
                try { blobColumn = (int)_pNclMetaFieldColumnIndex!.GetValue(fieldObj)!; } catch { }
                if (blobColumn >= 0)
                    LoadBlobField(self, parentBuffer, bufferIndexer, blobColumn);
            }

            // Then the FlowFields, through the same core BC's own
            // FlowFieldsHelper.CalcFieldsAsync now routes to (#1757). Entering at
            // recursionLevel 0 is what BC's RecordImplementation does — the core applies
            // BC's own `0 → 1, n → n+1` step before handing the level to
            // GetFilterFromMetaFilterCollection, so a formula that references another
            // FlowField re-enters here one level deeper and BC's >50 guard still bites.
            //
            // Only the FlowFields reach the core, exactly as BC hands `flowFields.ToArray()`
            // to FlowFieldsHelper.CalcFieldsAsync — and BC skips the call altogether when that
            // list is empty, so a `CalcFields(<a Blob>)` does not enter the FlowField pipeline
            // at all.
            var calculated = new List<Tuple<INavFieldMetadata, NavValue>>();
            if (flowFields.Count > 0)
                CalcFlowFieldValuesCore(
                    session, companyToken, parentBuffer,
                    tableState != null ? _pTableStateFiltersAndMarks?.GetValue(tableState) : null,
                    _fRecImplSecurityFiltering?.GetValue(self),
                    tableState != null ? _pTableStateReadIsolation?.GetValue(tableState) : null,
                    ToNclMetaFieldArray(flowFields), recursionLevel: 0, calculated);

            foreach (var item in calculated)
                bufferIndexer.SetValue(parentBuffer, item.Item2,
                    new object[] { ((NCLMetaField)item.Item1).ColumnIndex });

            return new System.Threading.Tasks.ValueTask<bool>(true);
        }
        // A runner out-of-scope signal is NOT a BC data error, so DataError.TrapError must not
        // turn it into `false`: letting the TrapError branches below swallow it would leave the
        // FlowField at its previous value with only a stderr line — the silent default
        // .claude/rules/loud-failures.md forbids. That is the whole reason for this rethrow;
        // the exception's own type buys nothing here. (An earlier version of this comment said
        // RunnerOutOfScopeException derives from plain Exception "so AL `asserterror` cannot
        // swallow it". It is a plain Exception, but asserterror catches it like any other error
        // — the runner's asserterror replacement is an unfiltered `catch (Exception)`. See
        // RunnerOutOfScopeException.cs's header and issue #2871.)
        catch (RunnerOutOfScopeException)
        {
            throw;
        }
        catch (TargetInvocationException tie) when (tie.InnerException is RunnerOutOfScopeException)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(tie.InnerException);
            return default;
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            // Header AND trace in ONE tagged write. Log.FilteredWriter matches its
            // `[Component]` pattern per write, and a stack trace carries no tag — split
            // across two calls the header is dropped at default verbosity and the frames
            // are not, which is how a green corpus run came to print 618 header-less
            // frame lines into every CI log (this is a trapped, expected AL error path,
            // not a failure). One write means the filter suppresses or keeps both together.
            Console.Error.WriteLine(
                $"[FlowFieldPatches] inner ex: {tie.InnerException.GetType().Name}: "
                + $"{tie.InnerException.Message}\n{tie.InnerException.StackTrace}");
            // Rethrow honoring DataError contract
            if (errorLevel == DataError.TrapError)
                return new System.Threading.Tasks.ValueTask<bool>(false);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(tie.InnerException);
            return default;
        }
        catch (Exception ex)
        {
            // One tagged write, header + trace — see the sibling catch above for why
            // splitting them leaks the frames past Log's filter. This catch is on the
            // ordinary AL error path (BC's own NavNCLStackOverflowException for a cyclic
            // CalcFormula, trapped by `asserterror` in a passing test), so the trace is
            // diagnostic detail, not evidence of a fault. The exception itself is still
            // rethrown / converted per BC's DataError contract below — unchanged.
            Console.Error.WriteLine(
                $"[FlowFieldPatches] ex: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            if (errorLevel == DataError.TrapError)
                return new System.Threading.Tasks.ValueTask<bool>(false);
            throw;
        }
    }

    /// <summary>
    /// #1757 — stands in for BC's own <c>FlowFieldsHelper.CalcFieldsAsync</c> (the 9-arg
    /// static). BC reaches it from two places the runner cannot otherwise serve:
    /// <c>GetFilterFromMetaFilterCollection</c>'s <c>FieldClass.FlowField</c> branch, which
    /// resolves a <c>field(&lt;FlowField&gt;)</c> where-condition by RECURSIVELY calculating
    /// the referenced FlowField, and <c>RecordIsWithinFilteredFlowFieldsAsync</c>. Hooking it
    /// (rather than pre-computing values into the parent buffer and pretending the value field
    /// is <c>Normal</c>) keeps BC's own dispatch in charge: the recursion, the ordering of the
    /// conditions and the two recursion guards are still BC's code, and every other BC caller
    /// of this method is served too.
    /// <para>Returns the <c>FieldDictionary&lt;NavValue&gt;</c> BC's callers index; boxed as
    /// <c>object</c> because that type is internal to Ncl. The Cecil rewrite casts it back and
    /// wraps it in the <c>ValueTask&lt;&gt;</c> the signature declares.</para>
    /// </summary>
    /// <remarks>
    /// <paramref name="onlyFieldsSourcedFromVirtualTables"/> is accepted and ignored, exactly as
    /// <see cref="RecordImpl_CalcFieldsAsync_3"/> already ignores it: the runner has no
    /// virtual-table FlowField source, so BC's virtual/non-virtual split has nothing to select
    /// between and every requested field is computed from the in-memory store.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static object FlowFieldsHelper_CalcFieldsAsync(
        object session, int companyToken, object recordBuffer, object? filtersAndMarks,
        Array fieldsToCalc, bool onlyFieldsSourcedFromVirtualTables,
        object? securityFiltering, object? alIsolationLevel, int recursionLevel)
    {
        if (_ctorFieldDictionary == null)
            throw new RunnerOutOfScopeException(
                "FlowFieldsHelper.CalcFieldsAsync",
                "not-yet-implemented — FieldDictionary<NavValue> could not be constructed on "
                + "this artifact, so a FlowField-valued CalcFormula where-condition cannot be "
                + "resolved the way BC resolves it (#1757)");

        var calculated = new List<Tuple<INavFieldMetadata, NavValue>>();
        if (fieldsToCalc != null && fieldsToCalc.Length > 0)
            CalcFlowFieldValuesCore(session, companyToken, recordBuffer, filtersAndMarks,
                securityFiltering, alIsolationLevel, fieldsToCalc, recursionLevel, calculated);

        // BC's callers index the result by field and would get a KeyNotFoundException for a
        // FlowField the core declined to compute (unmaterialised CalculationFormula, missing
        // source table, …). Name the field instead: a lookup failure three frames up inside Ncl
        // says nothing about which formula the runner could not answer.
        foreach (var fieldObj in fieldsToCalc ?? Array.Empty<object>())
        {
            if (fieldObj == null) continue;
            if (!Equals(_pNclMetaFieldFieldClass!.GetValue(fieldObj), _fcFlowField)) continue;
            if (calculated.Any(t => ReferenceEquals(t.Item1, fieldObj))) continue;
            var meta = fieldObj as NCLMetaField;
            throw new RunnerOutOfScopeException(
                "FlowFieldsHelper.CalcFieldsAsync",
                $"not-yet-implemented — the CalcFormula of '{meta?.FieldName}' on "
                + $"'{meta?.Parent?.TableName}' could not be evaluated, so the value BC would "
                + "have filtered on is unavailable; answering without it would silently change "
                + "the aggregate (#1757)");
        }

        return _ctorFieldDictionary.Invoke(new object[] { calculated.ToArray() });
    }

    // ── BC's own CalcFields field-list classification (#3012) ────────────────────────────
    //
    // RecordImplementation.CalcFieldsAsync(DataError, NCLMetaField[], bool) — the method
    // RecordImpl_CalcFieldsAsync_3 replaces — does NOT hand its field array straight on to
    // FlowFieldsHelper. It walks it first, splits it three ways, and throws on the first
    // field it cannot place (decompiled from Ncl 28.1.49838.53910):
    //
    //     foreach (NCLMetaField item in fields.Where(field => field.FieldActive))
    //     {
    //         if (item.FieldNclType == NavNclType.NavBlob)
    //         { GetWriteableBlobOnFieldAndEnsureInMutablePartOfBuffer(item); blobFields.Add(item); continue; }
    //
    //         if (item.FieldClass != FieldClass.FlowField)
    //             throw new NavCSideException(string.Format(CultureInfo.CurrentCulture,
    //                 Lang.MustBeAFlowField, await parentRecord.ALFieldCaptionAsync(item.FieldNo),
    //                 metaTable.TableCaptions.GetValueOrDefault()));
    //
    //         if (item.CalculationFormula == NCLMetaCalculationFormula.EmptyFormula)
    //             throw new NavCSideException(18023430, string.Format(CultureInfo.CurrentCulture,
    //                 Lang.MustDefineFormula, await parentRecord.ALFieldCaptionAsync(item.FieldNo),
    //                 metaTable.TableCaptions.GetValueOrDefault()));
    //
    //         flowFields.Add(item);
    //     }
    //
    // The runner used to have no equivalent: every entry point dropped a non-FlowField on the
    // floor with `if (!Equals(fieldClass, _fcFlowField)) continue;`, so `Rec.CalcFields("No.")`
    // and `Rec.CalcFields("<a FlowFilter>")` both did nothing at all and reported success —
    // exactly the silent default `loud-failures.md` exists to prevent, and measured green on
    // the runner while real BC refuses both.
    //
    // Two things deliberately NOT copied, each for a reason:
    //
    //   * BC's third refusal for this area, NavCSideException(18023494,
    //     Lang.OnlyFlowFieldsAllowedInCallsToCalcFields), lives one layer down in
    //     FlowFieldsHelper.GetDistinctSourceTablesFromFlowFields. AL never reaches it — this
    //     loop throws first — so it is reproduced in ValidateFlowFieldFormulas below, on the
    //     FlowFieldsHelper entry point, where BC's own other callers of that helper would.
    //
    //   * `.Where(field => field.FieldActive)`. NCLMetaField.FieldActive is computed from
    //     `fieldFlags & FieldFlags.Active`, and the runner's own NclMetaTableBuilder never
    //     populates fieldFlags at all, so it reads false for every field the runner builds.
    //     Filtering on it here would silently turn EVERY CalcFields into a no-op. Applying it
    //     is tracked separately rather than guessed at.

    /// <summary>
    /// Splits a <c>CalcFields</c> field list the way BC's own <c>RecordImplementation</c> does:
    /// BLOBs into <paramref name="blobFields"/>, FlowFields carrying a CalcFormula into
    /// <paramref name="flowFields"/>, and anything else straight into BC's own refusal.
    /// </summary>
    private static void ClassifyCalcFieldsRequest(
        Array fields, List<object> blobFields, List<object> flowFields)
    {
        foreach (var fieldObj in fields)
        {
            if (fieldObj == null) continue;

            if (_pNclMetaFieldFieldNclType != null && _nclTypeNavBlob != null
                && Equals(_pNclMetaFieldFieldNclType.GetValue(fieldObj), _nclTypeNavBlob))
            {
                blobFields.Add(fieldObj);
                continue;
            }

            if (!Equals(_pNclMetaFieldFieldClass!.GetValue(fieldObj), _fcFlowField))
                throw BuildCalcFieldsRefusal(fieldObj, errorNumber: null, "MustBeAFlowField",
                    "The {0} field in the {1} table must be a FlowField.");

            // BC compares against NCLMetaCalculationFormula.EmptyFormula. A null formula is not
            // a shape BC can produce — it means the runner failed to materialise one — but it
            // is the same observable situation for AL (there is no formula to evaluate), and
            // the alternative is the silent skip this whole block exists to remove.
            var formula = _pNclMetaFieldCalculationFormula!.GetValue(fieldObj);
            if (formula == null
                || (_fCalcFormulaEmpty != null && ReferenceEquals(formula, _fCalcFormulaEmpty.GetValue(null))))
                throw BuildCalcFieldsRefusal(fieldObj, errorNumber: 18023430, "MustDefineFormula",
                    "You must define a CalcFormula for the {0} FlowField in the {1} table.");

            flowFields.Add(fieldObj);
        }
    }

    /// <summary>
    /// Build one of BC's own <c>NavCSideException</c> refusals for a CalcFields field list.
    /// The wording comes from BC's own <c>Lang</c> resource class (in
    /// Microsoft.Dynamics.Nav.Language.dll, resolved by reflection — the runner does not
    /// reference it), so a BC version that rewords or localises the message is followed
    /// automatically instead of drifting from a copy kept here. <paramref name="fallbackFormat"/>
    /// is BC 28.1's own en-US text and is used only if the resource cannot be read, so AL still
    /// sees the refusal rather than a silent success.
    /// </summary>
    private static Exception BuildCalcFieldsRefusal(
        object fieldObj, int? errorNumber, string langResourceName, string fallbackFormat)
    {
        var format = ALDatabasePatches.LangString(langResourceName) ?? fallbackFormat;
        var message = string.Format(System.Globalization.CultureInfo.CurrentCulture, format,
            FieldCaptionForRefusal(fieldObj), TableCaptionForRefusal(fieldObj));

        try
        {
            var tCSide = ALDatabasePatches.ResolveNavCSideExceptionType();
            const BindingFlags CtorFlags =
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            if (tCSide != null && errorNumber is int number)
            {
                var ctorNumbered = tCSide.GetConstructor(
                    CtorFlags, null, new[] { typeof(int), typeof(string) }, null);
                if (ctorNumbered != null)
                    return (Exception)ctorNumbered.Invoke(new object[] { number, message });
            }

            if (tCSide != null)
            {
                var ctorPlain = tCSide.GetConstructor(
                    CtorFlags, null, new[] { typeof(string) }, null);
                if (ctorPlain != null)
                    return (Exception)ctorPlain.Invoke(new object[] { message });
            }
        }
        catch
        {
            // fall through — the refusal itself matters more than its exact CLR type
        }

        // Never swallow the refusal: an untyped exception still stops the call and still
        // carries BC's message, which is what AL's asserterror observes.
        return new InvalidOperationException(message);
    }

    /// <summary>
    /// BC formats these two messages with <c>NavRecord.ALFieldCaptionAsync(item.FieldNo)</c> and
    /// <c>metaTable.TableCaptions.GetValueOrDefault()</c>. <c>NCLMetaField.FieldCaption</c> is
    /// the property that async method reads, and <c>NCLMetaTable.TableCaptionSafe</c> is BC's
    /// own "captions, else the object name" accessor; both fall back to the name here so a
    /// table whose caption strings were never materialised still names the field and the table
    /// rather than producing "The  field in the  table must be a FlowField."
    /// </summary>
    private static string FieldCaptionForRefusal(object fieldObj)
    {
        var field = fieldObj as NCLMetaField;
        if (field == null) return string.Empty;
        try
        {
            var caption = field.FieldCaption;
            if (!string.IsNullOrEmpty(caption)) return caption;
        }
        catch { }
        return field.FieldName ?? string.Empty;
    }

    private static string TableCaptionForRefusal(object fieldObj)
    {
        var parent = (fieldObj as NCLMetaField)?.Parent;
        if (parent == null) return string.Empty;
        try
        {
            var pSafe = parent.GetType().GetProperty("TableCaptionSafe",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (pSafe?.GetValue(parent) is string safe && !string.IsNullOrEmpty(safe))
                return safe;
        }
        catch { }
        return parent.TableName ?? string.Empty;
    }

    /// <summary>
    /// BC's <c>NavCSideException(18023494, Lang.OnlyFlowFieldsAllowedInCallsToCalcFields)</c> —
    /// "The field {0} in the {1} table is not a FlowField or a BLOB field and cannot be passed
    /// in calls to CalcFields." Unlike the two RecordImplementation refusals this one is
    /// formatted with the raw <c>FieldName</c> / <c>Parent.TableName</c>, not with captions,
    /// so it is built separately rather than through BuildCalcFieldsRefusal.
    /// </summary>
    private static Exception BuildOnlyFlowFieldsAllowedRefusal(NCLMetaField field)
    {
        var format = ALDatabasePatches.LangString("OnlyFlowFieldsAllowedInCallsToCalcFields")
            ?? "The field {0} in the {1} table is not a FlowField or a BLOB field and cannot "
               + "be passed in calls to CalcFields.";
        var message = string.Format(System.Globalization.CultureInfo.CurrentCulture, format,
            field.FieldName, field.Parent?.TableName);

        try
        {
            var tCSide = ALDatabasePatches.ResolveNavCSideExceptionType();
            var ctor = tCSide?.GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { typeof(int), typeof(string) }, null);
            if (ctor != null)
                return (Exception)ctor.Invoke(new object[] { 18023494, message });
        }
        catch { }

        return new InvalidOperationException(message);
    }

    private static NCLMetaField[] ToNclMetaFieldArray(List<object> fields)
    {
        var arr = new NCLMetaField[fields.Count];
        for (int i = 0; i < fields.Count; i++) arr[i] = (NCLMetaField)fields[i];
        return arr;
    }

    /// <summary>
    /// Runs BC's own <c>FlowFieldsHelper.CheckFlowFieldProperties</c> over every FlowField in
    /// <paramref name="fields"/>, reproducing all five of its runtime refusals (#2970).
    /// </summary>
    /// <remarks>
    /// <para>
    /// BC raises these from <c>CheckFlowFieldProperties</c>, none of which the runner reached
    /// before this call existed, because the method sits inside
    /// <c>GetDistinctSourceTablesFromFlowFields</c> — on the path
    /// <see cref="FlowFieldsHelper_CalcFieldsAsync"/> replaces:
    /// </para>
    /// <list type="number">
    ///   <item><description><c>Count</c> into a field that is not Integer/BigInteger — 18023676.</description></item>
    ///   <item><description><c>Sum</c>/<c>Average</c> over a source that is not numeric — 18023674.</description></item>
    ///   <item><description><c>Sum</c>/<c>Average</c> whose types differ and cannot coerce — 18023443.</description></item>
    ///   <item><description><c>Exists</c> into a field that is not Boolean — 18023675.</description></item>
    ///   <item><description><c>Min</c>/<c>Max</c>/<c>Lookup</c> whose types differ and cannot coerce — 18023443.</description></item>
    /// </list>
    /// <para>
    /// Observable-equivalence justification (<c>loud-failures.md</c>'s audit obligation): none
    /// of the five rules is restated here. The refusal, its condition, its BC error number and
    /// its message text all come from BC's own method body, called with the same
    /// <c>NCLMetaField</c> BC would pass, so the runner cannot drift from BC by re-deriving a
    /// rule slightly differently — the failure mode that made a mistyped <c>average()</c>
    /// quietly produce a number here while a real service tier refused it on all eight legs.
    /// </para>
    /// <para>
    /// The field filter matches the aggregation loop's own skips exactly, so validation and
    /// aggregation see the same set: BLOBs are excluded (they are not FlowFields — BC's
    /// <c>RecordImplementation</c> strips them before <c>FlowFieldsHelper</c> ever sees them,
    /// and this replacement loads them separately), non-FlowField field classes are excluded,
    /// and so are <c>EmptyFormula</c> / <c>CalculationMethod.None</c>. BC's sixth refusal in
    /// this area — <c>OnlyFlowFieldsAllowedInCallsToCalcFields</c> (18023494), for a normal
    /// field passed to <c>CalcFields</c> — lives in the caller rather than in
    /// <c>CheckFlowFieldProperties</c> and has no service-tier measurement in the corpus, so it
    /// is tracked separately rather than guessed at here.
    /// </para>
    /// </remarks>
    private static void ValidateFlowFieldFormulas(Array fields)
    {
        foreach (var fieldObj in fields)
        {
            if (fieldObj is not NCLMetaField field) continue;

            if (_pNclMetaFieldFieldNclType != null && _nclTypeNavBlob != null
                && Equals(_pNclMetaFieldFieldNclType.GetValue(fieldObj), _nclTypeNavBlob))
                continue;

            // #3012 — BC's GetDistinctSourceTablesFromFlowFields refuses a non-FlowField here,
            // as the very first thing it does per field, AFTER its caller has filtered BLOBs
            // out (`fieldsToCalc.Where(f => f.FieldNclType != NavNclType.NavBlob)`):
            //
            //     if (flowField.FieldClass != FieldClass.FlowField)
            //         throw new NavCSideException(18023494, string.Format(...,
            //             Lang.OnlyFlowFieldsAllowedInCallsToCalcFields,
            //             flowField.FieldName, flowField.Parent.TableName));
            //
            // A record-level CalcFields never gets this far — ClassifyCalcFieldsRequest above
            // has already refused with RecordImplementation's own wording, which is what AL
            // sees. This guards the OTHER entry point, FlowFieldsHelper_CalcFieldsAsync, which
            // BC's own code re-enters (GetFilterFromMetaFilterCollection resolving a
            // `field(<a FlowField>)` condition, RecordIsWithinFilteredFlowFieldsAsync). Those
            // callers pass FlowFields by construction, so this is the runner declining to be
            // more permissive than BC on a path BC also checks — not a skip.
            if (!Equals(_pNclMetaFieldFieldClass!.GetValue(fieldObj), _fcFlowField))
                throw BuildOnlyFlowFieldsAllowedRefusal(field);

            var formula = _pNclMetaFieldCalculationFormula!.GetValue(fieldObj);
            if (formula == null) continue;
            if (_fCalcFormulaEmpty != null && ReferenceEquals(formula, _fCalcFormulaEmpty.GetValue(null)))
                continue;
            if (Equals(_pCalcFormulaCalculationMethod!.GetValue(formula), _cmNone)) continue;

            // Refuse loudly rather than skip: silently not validating is precisely how the
            // runner came to compute a value real BC rejects, so an artifact without the
            // method must say so instead of answering permissively (loud-failures.md).
            if (_checkFlowFieldProperties == null)
                throw new RunnerOutOfScopeException(
                    "FlowFieldsHelper.CheckFlowFieldProperties",
                    "not-yet-implemented — BC's own CalcFormula validator is unavailable on this "
                    + "artifact, and skipping it would let a CalcFormula real BC refuses compute "
                    + "a value here instead (#2970)");

            _checkFlowFieldProperties(field);
        }
    }

    /// <summary>
    /// The shared FlowField evaluation both entry points run: the
    /// <see cref="RecordImpl_CalcFieldsAsync_3"/> hook (which then writes the values into the
    /// record's buffer) and the <see cref="FlowFieldsHelper_CalcFieldsAsync"/> hook (which
    /// returns them as a <c>FieldDictionary</c>). Everything about a formula — its
    /// where-conditions, the source-row enumeration, the aggregation and the negation — is
    /// evaluated once, here.
    /// </summary>
    private static void CalcFlowFieldValuesCore(
        object session, int companyToken, object parentBuffer, object? parentFiltersAndMarks,
        object? securityFiltering, object? alIsolationLevel, Array fields, int recursionLevel,
        List<Tuple<INavFieldMetadata, NavValue>> results)
    {
        // ── BC's two recursion guards, in BC's order ────────────────────────────────
        // Both live in the method chain this replacement stands in for, and both exist
        // precisely because a `field(<FlowField>)` where-condition makes the calculation
        // re-entrant. Dropping them would turn a self-referencing or mutually-referencing
        // formula into a native stack overflow, which kills the process instead of failing
        // the test — the opposite of what BC does.
        if (recursionLevel > MaxRecursionLevel)
            throw NewStackOverflowException();
        if (FieldsAndFormulaAreSelfReferencing(fields))
            throw NewStackOverflowException();

        // BC's own step: the level handed DOWN to the where-condition resolution (and hence
        // to any nested CalcFieldsAsync) is `recursionLevel != 0 ? recursionLevel + 1 : 1`.
        // Copied rather than simplified to `recursionLevel + 1` because the two agree only
        // for 0, and the guard above compares against the level BC would have produced.
        int nestedRecursionLevel = recursionLevel != 0 ? checked(recursionLevel + 1) : 1;

        // ── BC's CalcFormula validation, before ANY aggregate is computed (#2970) ──────
        // BC validates every FlowField handed to CalcFields while it is BUILDING the distinct
        // source tables, and only then runs the queries: GetDistinctSourceTablesFromFlowFields
        // loops the fields calling DistinctSourceTable.AddField, whose very first statement is
        // CheckFlowFieldProperties(field), and the aggregation happens afterwards in
        // CalcFieldsFromNonVirtualTablesAsync. So `CalcFields(GoodField, BadField)` computes
        // NOTHING in BC — it errors before the first query. A per-field check folded into the
        // aggregation loop below would instead compute GoodField and then throw, leaving a
        // value behind that BC never writes. Hence a separate pass, in field order.
        ValidateFlowFieldFormulas(fields);

        // The skeleton DAS — needed to obtain source-table TempTableDataProvider
        var dataAccessSource = _fSessionDataAccessSource?.GetValue(session);

        foreach (var fieldObj in fields)
        {
            if (fieldObj == null) continue;
            var fieldClass = _pNclMetaFieldFieldClass!.GetValue(fieldObj);

            // BLOBs are not FlowFields and are loaded by the RecordImplementation entry
            // point before this core runs (BC filters them out of its own pipeline too).
            if (_pNclMetaFieldFieldNclType != null && _nclTypeNavBlob != null
                && Equals(_pNclMetaFieldFieldNclType.GetValue(fieldObj), _nclTypeNavBlob))
                continue;

            if (!Equals(fieldClass, _fcFlowField)) continue;

            var formula = _pNclMetaFieldCalculationFormula!.GetValue(fieldObj);
            if (formula == null) continue;
            if (_fCalcFormulaEmpty != null && ReferenceEquals(formula, _fCalcFormulaEmpty.GetValue(null)))
                continue;

            var calcMethod = _pCalcFormulaCalculationMethod!.GetValue(formula);
            if (Equals(calcMethod, _cmNone)) continue;

            // Source field/table from formula IDs (avoid SourceField resolution path, which can fail
            // on the skeleton app-group metadata lookup even for inserted records).
            NCLMetaField? srcFieldMeta = null;
            int srcFieldColumn = -1;
            NCLMetaTable? srcTable = null;

            var tableIdObj = _pCalcFormulaTableId?.GetValue(formula);
            var fieldIdObj = _pCalcFormulaFieldId?.GetValue(formula);
            int tableId = tableIdObj is int tid ? tid : 0;
            int fieldId = fieldIdObj is int fid ? fid : 0;

            if (tableId != 0)
                srcTable = ResolveTableById(tableId);

            if (srcTable != null && fieldId != 0)
            {
                try
                {
                    srcFieldMeta = srcTable.GetFieldByNo(fieldId, trapError: true);
                }
                catch
                {
                    srcFieldMeta = null;
                }
            }
            if (srcFieldMeta != null)
                srcFieldColumn = srcFieldMeta.ColumnIndex;

            // Source TempTableDataProvider — call our replacement directly, NOT via reflection
            // (MethodInfo.Invoke bypasses JmpHook and gets the original empty-store impl).
            if (srcTable == null) continue;
            object? srcTtdp = null;
            try
            {
                var srcDataAccess = AlRunner.Patches.RecordPatches
                    .NavDataAccessSource_GetDataAccessForTable(dataAccessSource!, srcTable, false);
                if (srcDataAccess != null && _pDataAccessDataProvider != null)
                    srcTtdp = _pDataAccessDataProvider.GetValue(srcDataAccess) ?? srcDataAccess;
            }
            catch { }
            if (srcTtdp == null) continue;

            // #2648 — the Date virtual table (2000000007) materialises its rows PER REQUEST, and
            // this method is a read of the source table's store that carries no request the Date
            // guards can see: it goes from the handout straight to TempTableDataProvider.Filter,
            // past DataAccess and past the provider's own public read methods. Measured without
            // this call, on this branch: `count(Date where …)` returned 0 instead of 73,049,
            // `exist(Date where …)` No instead of Yes, `min("Date"."Period Start")` blank instead
            // of 1900-01-01. The call materialises the whole window once per Date store and is a
            // ConditionalWeakTable miss for every other source table.
            AlRunner.Patches.RecordPatches.EnsureDateStoreFullyMaterialised(srcTtdp);

            // Resolve the formula's where-conditions into a FiltersAndMarks over the SOURCE
            // table, using BC's own FlowFieldsHelper.GetFilterFromMetaFilterCollection.
            //
            // Nothing about condition semantics is restated here. That one method already
            // knows how to turn every shape into a FilterExpression — FIELD (equality
            // against the parent's value, with cross-type transfer), CONST, FILTER, and
            // the flow-filter forms #1716 added: a FlowFilter value field contributes the
            // CALLER's filter, `field(filter(X))` parses the parent's value as a filter
            // expression, `field(upperlimit(X))` keeps only that filter's upper bound, and
            // an unset/blank one contributes nothing at all. It also owns the ordering
            // rule that a later mode-carrying condition REPLACES an earlier link on the
            // same source field rather than ANDing with it (the "G/L Account".Totaling
            // behaviour). Re-deriving any of that here is how it goes quietly wrong.
            //
            // The resulting dictionary is handed to TempTableDataProvider.Filter, so BC's
            // RecordBufferEvaluatorVisitor — the same code every SetFilter/FindSet goes
            // through — decides which rows match.
            var filters = _pCalcFormulaFilters!.GetValue(formula);
            object? srcFiltersAndMarks = _emptyFm;
            if (filters != null)
            {
                if (_mGetFilterFromMetaFilterCollection == null || _ctorFiltersAndMarks == null
                    || _pTableStateFiltersAndMarks == null)
                    throw new RunnerOutOfScopeException(
                        "NCLMetaCalculationFormula.Filters",
                        "not-yet-implemented — BC's FlowFieldsHelper.GetFilterFromMetaFilterCollection "
                        + "is unavailable on this artifact, and guessing a CalcFormula's "
                        + "where-conditions would silently change the aggregate (#1716)");

                // #1757 — the one shape that used to be refused here: a `field(...)` link
                // whose VALUE field is itself a FlowField. BC resolves it by recursively
                // calling FlowFieldsHelper.CalcFieldsAsync, and that static is now hooked
                // too (FlowFieldsHelper_CalcFieldsAsync above), so the branch below re-enters
                // this same core one level deeper and comes back with a real value instead
                // of NREing in the async pipeline. Passing `nestedRecursionLevel` — not a
                // hardcoded 0 — is what keeps BC's >50 guard able to fire on a cycle.
                object? dict;
                try
                {
                    dict = _mGetFilterFromMetaFilterCollection.Invoke(null, new object?[]
                    {
                        session,
                        companyToken,
                        parentBuffer,
                        parentFiltersAndMarks,
                        securityFiltering
                            ?? Enum.ToObject(_mGetFilterFromMetaFilterCollection.GetParameters()[4].ParameterType, 0),
                        filters,
                        alIsolationLevel
                            ?? Enum.ToObject(_mGetFilterFromMetaFilterCollection.GetParameters()[6].ParameterType, 0),
                        nestedRecursionLevel,
                    });
                }
                catch (TargetInvocationException tie) when (tie.InnerException != null)
                {
                    // Reflection wraps whatever BC threw, and with #1757 this call can now
                    // recurse back into this same core — so a cycle detected 50 levels down
                    // would otherwise surface as 50 nested TargetInvocationExceptions whose
                    // message is "Exception has been thrown by the target of an invocation".
                    // AL must see BC's own error (NavNCLStackOverflowException's "…can be
                    // caused by recursive function calls…", NavCSideFilterException for a
                    // rejected upperlimit() range, …), so the wrapper is stripped at every
                    // level, preserving the original stack.
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo
                        .Throw(tie.InnerException);
                    throw; // unreachable; satisfies definite assignment
                }
                // null = every condition resolved to "no constraint" (e.g. only an unset
                // flow filter), which is BC's answer for "aggregate the whole table".
                if (dict != null)
                    srcFiltersAndMarks = _ctorFiltersAndMarks.Invoke(new[] { dict });
            }

            // Enumerate source rows via TempTableDataProvider.Filter to mirror the runner's
            // CalcNumeric path (company-scoped, key-ordered, current in-memory rows).
            IEnumerable? rows = null;
            try
            {
                var sortingFields = _fTtdpPrimaryKeySortingFields?.GetValue(srcTtdp);
                rows = _mTtdpFilter?.Invoke(srcTtdp, new object?[]
                {
                    companyToken,
                    srcFiltersAndMarks,
                    null,
                    sortingFields,
                    false
                }) as IEnumerable;
            }
            catch (Exception ex)
            {
                // A throw here is BC's filter machinery rejecting the resolved conditions
                // (e.g. NavCSideFilterException for an upperlimit() over a non-contiguous
                // range). Surfacing it is the point — swallowing it would leave the
                // FlowField at its previous value with nothing said.
                var inner = ex is System.Reflection.TargetInvocationException tie
                    ? tie.InnerException ?? ex : ex;
                Console.Error.WriteLine(
                    $"[FlowFieldPatches] source-table filter failed for table {tableId}: "
                    + $"{inner.GetType().Name}: {inner.Message}");
                // Not `throw inner` (#2948): a bare rethrow resets the trace to this line,
                // erasing BC's own filter-machinery frames — the very frames that say WHICH
                // condition it rejected. The sibling catch forty lines above already used
                // ExceptionDispatchInfo; this one did not, and that asymmetry is what made
                // #2925's four-test cluster look unrelated to its own root cause.
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(inner).Throw();
                throw; // unreachable
            }
            if (rows == null)
            {
                continue;
            }

            // Aggregate
            int matchCount = 0;
            int totalSeen = 0;
            decimal sum = 0m;
            NavValue? minV = null, maxV = null, lookupV = null;
            bool anyMatch = false;

            foreach (var row in rows)
            {
                if (row == null) continue;
                totalSeen++;
                var rowType = row.GetType();
                var rowIndexer = rowType.GetProperty("Item",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, typeof(NavValue), new[] { typeof(int) }, null)
                    ?? rowType.GetProperty("Item",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (rowIndexer == null) continue;

                // No per-row predicate here any more: TempTableDataProvider.Filter was
                // handed the resolved FiltersAndMarks above, so every row reaching this
                // point has already passed BC's own where-condition evaluation.
                anyMatch = true;
                matchCount++;

                if (Equals(calcMethod, _cmExist))
                {
                    // Short-circuit
                    break;
                }
                if (srcFieldColumn >= 0 && (Equals(calcMethod, _cmSum) || Equals(calcMethod, _cmAverage)
                    || Equals(calcMethod, _cmMin) || Equals(calcMethod, _cmMax) || Equals(calcMethod, _cmLookup)))
                {
                    var srcVal = ReadBufferFieldValue(row, rowIndexer, srcFieldColumn, srcFieldMeta);
                    if (srcVal == null) continue;
                    if (Equals(calcMethod, _cmSum) || Equals(calcMethod, _cmAverage))
                    {
                        try { sum = checked(sum + (decimal)srcVal.ToDecimal()); }
                        catch { /* non-numeric — skip */ }
                    }
                    else if (Equals(calcMethod, _cmMin))
                    {
                        if (minV == null || NavValueCompare(srcVal, minV) < 0) minV = srcVal;
                    }
                    else if (Equals(calcMethod, _cmMax))
                    {
                        if (maxV == null || NavValueCompare(srcVal, maxV) > 0) maxV = srcVal;
                    }
                    else if (Equals(calcMethod, _cmLookup))
                    {
                        if (lookupV == null) lookupV = srcVal;
                        // first match wins; could break, but we keep counting for diagnostics
                        break;
                    }
                }
            }

            // Build result
            bool negate = (bool)(_pCalcFormulaNegateResult?.GetValue(formula) ?? false);
            NavValue? result;

            if (Equals(calcMethod, _cmCount))
                result = NavValue.CreateNavValueFromObject((NCLMetaField)fieldObj, matchCount);
            else if (Equals(calcMethod, _cmExist))
                // #2323 — the sign is applied HERE for exist, not through NegateValue below,
                // because BC applies it here too: FlowFieldsHelper's Exists branch builds
                //   NegateResult ? NavBoolean.Create(!exists) : NavBoolean.Create(exists)
                // and the virtual-table path is the same shape. See the NegateValue comment.
                result = NavValue.CreateNavValueFromObject((NCLMetaField)fieldObj,
                    negate ? !anyMatch : anyMatch);
            else if (Equals(calcMethod, _cmSum))
                result = NavValue.CreateNavValueFromObject((NCLMetaField)fieldObj, CoerceSumResult(sum));
            else if (Equals(calcMethod, _cmAverage))
                result = NavValue.CreateNavValueFromObject((NCLMetaField)fieldObj,
                    matchCount > 0 ? sum / matchCount : 0m);
            else if (Equals(calcMethod, _cmMin))
                result = minV ?? TypedDefaultForField(fieldObj) ?? NavValue.CreateNavValueFromObject((NCLMetaField)fieldObj, 0);
            else if (Equals(calcMethod, _cmMax))
                result = maxV ?? TypedDefaultForField(fieldObj) ?? NavValue.CreateNavValueFromObject((NCLMetaField)fieldObj, 0);
            else if (Equals(calcMethod, _cmLookup))
                result = lookupV ?? TypedDefaultForField(fieldObj) ?? NavValue.CreateNavValueFromObject((NCLMetaField)fieldObj, "");
            else
            {
                Console.Error.WriteLine($"[FlowFieldPatches] unsupported CalculationMethod {calcMethod}");
                continue;
            }

            // #1708 — `CalcFormula = -sum(...)`. The negation is BC's own
            // NCLMetaCalculationFormula.NegateValue rather than a local `-x`, so the numeric
            // CalculationMethods get exactly the semantics BC gives them.
            //
            // #2323 — but NOT for exist. NegateValue switches on the SOURCE FIELD's type,
            // not the value's:
            //
            //     public NavValue NegateValue(NavValue value) => SourceField.FieldNavType switch
            //     {
            //         NavType.Decimal    => NavDecimal.Create(-((NavDecimal)value).Value),
            //         NavType.BigInteger => NavBigInteger.Create(-((NavBigInteger)value).Value),
            //         NavType.Integer    => NavInteger.Create(-((NavInteger)value).Value),
            //         _ => value,
            //     };
            //
            // An exist FlowField is Boolean by construction (CheckFlowFieldProperties throws
            // FlowFieldMustBeBooleanError otherwise) while its source field is whatever the
            // where clause names, so the two never agree. Routing exist through here was wrong
            // in BOTH directions: a numeric source field made the cast throw
            // InvalidCastException (Purch. Inv. Header."Closed", whose source field is a
            // BigInteger), and a non-numeric one fell into `_ => value` and returned the
            // UN-negated Boolean — the exact opposite of the truth, silently
            // (Item."Cost is Posted to G/L", whose source field is a Code).
            //
            // Real BC never reaches NegateValue for exist; it negates the Boolean logically
            // at construction, which is what the _cmExist branch above now does. Every BC site
            // that DOES call NegateValue builds its value from the source field first
            // (ExecuteAggregateAsync's CreateNavValueFromReader(SourceField, i)), or only
            // handles Count/Sum/Average, so the types agree there by construction.
            if (negate && result != null && !Equals(calcMethod, _cmExist))
                result = NegateAggregateResult(formula!, result, "Record.CalcFields");

            if (result != null)
                results.Add(Tuple.Create((INavFieldMetadata)(NCLMetaField)fieldObj, result));
        }
    }

    /// <summary>
    /// #2300 — compute a FlowField's value for one QUERY result row, the same way a `Record.
    /// CalcFields` call computes it for one record row (via <see cref="CalcFlowFieldValuesCore"/>
    /// — the same source-row enumeration, formula-filter resolution and aggregation, just entered
    /// from the query projection layer instead of RecordImplementation). BC's own query engine
    /// answers this by naming the FlowField a synthesized `OuterApply` sub-dataitem
    /// (<c>NCLMetaQuery.CreateSubQueryForFlowFieldCalculation</c>) and letting SQL execute it —
    /// the runner has no SQL to run that sub-query against, so it computes the value directly
    /// against the in-memory store instead. <paramref name="rowBuffer"/> is the QUERY row
    /// (<c>ReadOnlyRecordBuffer</c>, boxed as <c>object</c> since QueryProjection.cs isn't allowed
    /// to hand a typed reference across the same isolation boundary that keeps AlRunner.QueryJoin
    /// Ncl-free) — it satisfies BC's own <c>IRecordBuffer</c> the same as a record's
    /// <c>MutableRecordBuffer</c> does, which is what <c>GetFilterFromMetaFilterCollection</c>
    /// actually requires (its parameter type is the interface, not the concrete buffer type), so
    /// CalcFlowFieldValuesCore needs no changes to accept it.
    ///
    /// #2925 — <paramref name="flowFiltersAndMarks"/> carries the QUERY's own flow filters (an
    /// AL <c>filter(Name; "Some Flow Filter")</c> element, or a static <c>ColumnFilter</c> on
    /// one), keyed by the FlowFilter <c>NCLMetaField</c>, exactly the way a record's
    /// <c>FiltersAndMarks</c> carries the ones <c>Record.SetRange("Date Filter", ...)</c> sets.
    /// It is what BC's own <c>FlowFieldsHelper.GetFilterFromMetaFilterCollection</c>
    /// dereferences UNGUARDED for a <c>FieldClass.FlowFilter</c> where-condition
    /// (<c>GetFlowFilterBasedFilter(metaFilter, filtersAndMarks.Filters, session)</c>), so
    /// passing null here — which this method used to do — NREs inside BC for every CalcFormula
    /// carrying a flow-filter condition (e.g. <c>Cust. Ledger Entry."Remaining Amt. (LCY)"</c>,
    /// whose formula reads <c>upperlimit("Date Filter")</c>).
    ///
    /// A null argument means "this query set no flow filter", and is answered with BC's own
    /// <c>FiltersAndMarks.Empty</c> — whose <c>Filters</c> is itself null, which is precisely
    /// the input <c>GetFlowFilterBasedFilter</c> reads as "flow filter unset → contributes no
    /// constraint" (it returns null, and the caller's <c>IsNullOrConstantTrue()</c> skips it).
    /// So the unset case is decided by BC's code, not by a runner-side assumption about it.
    /// </summary>
    internal static NavValue? CalcOneFlowFieldForQueryRow(
        object rowBuffer, NCLMetaField flowFieldMeta, object? flowFiltersAndMarks = null)
    {
        // NavCurrentThread.Session must resolve on every real test run (CalcFlowFieldValuesCore
        // below is unreachable without it, and every other FlowField entry point in this file —
        // RecordImpl_CalcFieldsAsync_3 above — relies on the SAME static resolving). Swallowing
        // a reflection failure here and returning null would silently read a query FlowField
        // column as unset/default instead of its calculated value — the loud-failures.md silent
        // default this file exists to avoid everywhere else. Fail loudly instead, naming the
        // surface and the field, so a genuine artifact incompatibility is visible rather than
        // read back as "the value is 0/empty".
        var tNCT = flowFieldMeta.GetType().Assembly.GetType("Microsoft.Dynamics.Nav.Runtime.NavCurrentThread")
            ?? throw new InvalidOperationException(
                $"CalcOneFlowFieldForQueryRow('{flowFieldMeta.FieldName}' on "
                + $"'{flowFieldMeta.Parent?.TableName}'): Microsoft.Dynamics.Nav.Runtime.NavCurrentThread "
                + "type not found in the Ncl assembly — cannot resolve the current NavSession to "
                + "compute this query FlowField column.");
        var pSess = tNCT.GetProperty("Session", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                $"CalcOneFlowFieldForQueryRow('{flowFieldMeta.FieldName}' on "
                + $"'{flowFieldMeta.Parent?.TableName}'): NavCurrentThread.Session property not "
                + "found — cannot resolve the current NavSession to compute this query FlowField column.");
        var session = pSess.GetValue(null)
            ?? throw new InvalidOperationException(
                $"CalcOneFlowFieldForQueryRow('{flowFieldMeta.FieldName}' on "
                + $"'{flowFieldMeta.Parent?.TableName}'): NavCurrentThread.Session returned null — "
                + "no current session to compute this query FlowField column against.");

        // The runner is single-company; token 0 is the runner's own unnamed company (see
        // RecordPatches.cs's companyTokens skeleton-state comment) — the same value every other
        // FlowField/query code path in this runner uses when no per-record company token is
        // available.
        // #2925: never null — see the summary above. Resolving FiltersAndMarks.Empty is part of
        // Register(); if THAT failed, say so instead of handing BC a null it dereferences (the
        // NRE this parameter exists to remove) or silently answering with an unfiltered total.
        var parentFm = flowFiltersAndMarks ?? _emptyFm
            ?? throw new InvalidOperationException(
                $"CalcOneFlowFieldForQueryRow('{flowFieldMeta.FieldName}' on "
                + $"'{flowFieldMeta.Parent?.TableName}'): Microsoft.Dynamics.Nav.Runtime."
                + "FiltersAndMarks.Empty could not be resolved on this artifact, so the "
                + "FlowField's where-conditions cannot be evaluated against BC's own helper.");

        var results = new List<Tuple<INavFieldMetadata, NavValue>>();
        CalcFlowFieldValuesCore(session, companyToken: 0, rowBuffer,
            parentFiltersAndMarks: parentFm, securityFiltering: null, alIsolationLevel: null,
            new NCLMetaField[] { flowFieldMeta }, recursionLevel: 0, results);
        return results.Count > 0 ? results[0].Item2 : null;
    }

    // ── BC recursion guards ──────────────────────────────────────────────────
    // FlowFieldsHelper's own constant, and its own exception. Both guards are BC's, not the
    // runner's: a self-referencing or mutually-referencing CalcFormula must produce exactly
    // the error a service tier produces ("There is insufficient memory to execute this
    // function. This can be caused by recursive function calls. …"), never a native stack
    // overflow — which would take the whole test process down instead of failing one test.
    private const int MaxRecursionLevel = 50;

    private static Exception NewStackOverflowException()
    {
        // BC additionally calls session.Diagnostics.SendExceptionTag(...) before throwing.
        // That is telemetry, not behaviour: it has no effect on the value or the error AL
        // observes, and the skeleton session's Diagnostics is not wired up. The exception
        // itself — type, message and the fact that it is thrown rather than trapped — is
        // what AL sees, and that is reproduced exactly.
        return new Microsoft.Dynamics.Nav.Types.Exceptions.NavNCLStackOverflowException();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static void WriteEmptyFlowFieldValue(object parentBuffer, PropertyInfo bufferIndexer, object fieldObj, int columnIndex)
    {
        if (columnIndex < 0)
            return;
        try
        {
            var emptyValue = fieldObj.GetType().GetProperty("EmptyValue",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(fieldObj) as NavValue;
            if (emptyValue != null)
                bufferIndexer.SetValue(parentBuffer, emptyValue, new object[] { columnIndex });
        }
        catch
        {
            // Best effort only; caller continues with default buffer state.
        }
    }

    // Called by RecordImpl_CalcFieldsAsync_3 when a BLOB field is in the CalcFields list.
    // Mirrors RecordImplementation.CalcFieldsAsync blob branch:
    //   GetWriteableBlobOnFieldAndEnsureInMutablePartOfBuffer + GetBlobContentAsync
    // but goes directly to the TempTableDataProvider primaryTree to avoid the
    // transactional-cache layer that isn't needed for temp tables.
    private static void LoadBlobField(object self, object parentBuffer, PropertyInfo bufferIndexer, int fieldIdx)
    {
        try
        {
            // Step 1a: data access + primary-key lookup, done ahead of the placeholder
            // sizing in Step 1b below so the ineligibility check (#1765 / corpus 60944)
            // can influence how the placeholder is sized. Unlike the original ordering,
            // this must NOT early-return before Step 1b runs — Step 1b's placeholder
            // creation happened unconditionally before this lookup existed, and callers
            // (RecordImpl_CalcFieldsAsync_3) rely on `parentBuffer[fieldIdx]` always
            // ending up a non-null NavBLOB after this method returns, found-or-not.
            var dataAccess = _fRecImplDataAccess?.GetValue(self);
            var dataProvider = dataAccess != null ? _pDataAccessDataProvider?.GetValue(dataAccess) : null;
            var canLookUp = dataProvider != null && _mTtdpTryGetValue != null && _mMutableBufferGetRecordId != null;
            object? storedBuffer = null;
            if (canLookUp)
            {
                var recordId = _mMutableBufferGetRecordId!.Invoke(parentBuffer, null);
                var tryGetArgs = new object?[] { recordId, null };
                if (_mTtdpTryGetValue!.Invoke(dataProvider, tryGetArgs) is true)
                    storedBuffer = tryGetArgs[1];
            }

            // Issue #1765 / corpus 60944: a temporary record's BLOB carried over by a
            // Rename() (not freshly dirtied by it) is lost on real BC — HasValue() reads
            // false after Get()+CalcFields() on the renamed row, even though the same
            // value round-trips fine without the Rename in between (60940). Ncl's own
            // store still faithfully holds the bytes (see BlobStoreIsolationPatches.
            // OnModifyAllTrees), so reproduce the loss here rather than reloading it —
            // matching what real BC's temporary JIT-load actually returns after a
            // rename. Checked by (row, field index), not by the BLOB value object: see
            // the comment above OnModifyAllTrees for why value-object identity fails —
            // Get()'s own Find()-based read materialises a DIFFERENT NavBLOB instance
            // for this record's own buffer than the one the tree returns.
            var ineligible = BlobStoreIsolationPatches.IsFieldIneligibleForCalcFieldsReload(storedBuffer, fieldIdx);

            // Step 1b: ensure a writable NavBLOB is in changedValues (mirrors GetWriteableBlobOnField...)
            var navBLOB = _mMutableBufferGetChangedFieldValue?.Invoke(parentBuffer, new object[] { fieldIdx }) as NavBLOB;
            if (navBLOB == null)
            {
                // A marked field must present as absent altogether — real BC's
                // HasValue() reads false, not "present but unloaded" — so size the
                // placeholder as zero-length (NavBLOB.Default()) rather than from
                // `original.ALLength`, which alone makes ALHasValue true (GetLength()
                // falls back to sizeWhenNoContents when contents is null — see
                // Microsoft.Dynamics.Nav.Runtime.NavBLOB) regardless of whether the
                // byte-copy below ever runs.
                var original = ineligible
                    ? null
                    : _mMutableBufferGetOriginalValue?.Invoke(parentBuffer, new object[] { fieldIdx }) as NavBLOB;
                navBLOB = original != null ? new NavBLOB(original.ALLength) : NavBLOB.Default();
                bufferIndexer.SetValue(parentBuffer, navBLOB, new object[] { fieldIdx });
            }

            if (!canLookUp || ineligible || storedBuffer == null) return;

            // Step 2: copy blob data from stored buffer into the writable NavBLOB
            var storedIndexer = storedBuffer.GetType().GetProperty("Item",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, typeof(NavValue), new[] { typeof(int) }, null);
            var storedBLOB = storedIndexer?.GetValue(storedBuffer, new object[] { fieldIdx }) as NavBLOB;
            if (storedBLOB == null || storedBLOB.IsZeroOrEmpty) return;

            // The stored row and the record's mutable buffer can hold the SAME NavBLOB
            // instance. Ncl's writeable-blob path hands back the row's own NavBLOB (rather
            // than a copy) when the field carried no value at Insert time, so a subsequent
            // `Content.CreateOutStream(o); o.WriteText(...)` mutates that one object in
            // place and both sides observe it. Verified by object identity: for a row
            // inserted with an untouched BLOB, storedBLOB and navBLOB are reference-equal;
            // for a row inserted with content already in the BLOB they are distinct.
            //
            // AssignFromStream(storedBLOB.GetStream()) is then a self-copy: the target is
            // reset before the source stream — which reads from that same, now-emptied
            // target — is drained, so the blob ends up zero-length. That is what made
            // `Insert` → `CreateOutStream`+`Write` → `CalcFields(Blob)` read back '' while
            // real BC keeps the uncommitted write (issue #1724).
            //
            // When both sides are the same object there is by definition nothing to load:
            // the buffer already holds exactly the bytes the copy would have produced.
            // Skipping keeps CalcFields observably equivalent to real BC for this shape,
            // and leaves every non-aliased load (Get() → CalcFields()) on the copy path.
            //
            // Since #1751 the aliasing only ever happens for a `temporary` record, which
            // is exactly the shape real BC aliases too: corpus 60940 pins that a temporary
            // row DOES observe an uncommitted BLOB write while a database-backed row does
            // not. BlobStoreIsolationPatches detaches the stored BLOB at Insert for
            // database-backed providers only, so this guard is now the temporary path's
            // guard — and it stays correct for exactly the reason above: when both sides
            // are the same object the buffer already holds the bytes a copy would produce.
            if (ReferenceEquals(storedBLOB, navBLOB)) return;

            navBLOB.AssignFromStream(storedBLOB.GetStream());
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            Console.Error.WriteLine($"[FlowFieldPatches] LoadBlobField({fieldIdx}) inner ex: {tie.InnerException.GetType().Name}: {tie.InnerException.Message}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FlowFieldPatches] LoadBlobField({fieldIdx}) ex: {ex.GetType().Name}: {ex.Message}");
        }
    }


    private static NCLMetaTable? ResolveTableById(int tableId)
    {
        try
        {
            return NavGlobal.NCLMetadata.GetMetaTableById(tableId, requireCompiled: false);
        }
        catch
        {
            return null;
        }
    }

    // Produce a typed default NavValue for an NCLMetaField, matching the field's own type
    // (0 for Integer, '' for Text, 0D for Decimal, …) — the SAME real BC factory
    // RecordPatches.QueryJoin.cs already reuses for its unmatched-LeftOuterJoin-column case
    // (NavValue.GetDefaultNavValue(INavValueMetadata, nullSupport:false); NCLMetaField IS an
    // INavValueMetadata). Used for Min/Max/Lookup's "no source row matched the filter" case:
    // that previously hardcoded a bare `0` (Min/Max) or `""` (Lookup) regardless of the
    // TARGET field's real type — faithful for an Integer target, but NavValue.
    // CreateNavValueFromObject then throws NavNCLEvaluateException for any other type (a Date/
    // Text/Decimal Min or Max field, or — the case that surfaced this — an Integer Lookup
    // field, since "" doesn't evaluate to Integer either). GetDefaultNavValue always returns
    // the field's OWN type's default, so this is correct for every field type, not just the
    // one the original hardcoded literal happened to match.
    //
    // internal (not private): RecordPatches.QueryProjection.cs's Min/Max query-column
    // aggregation (issue #2137) reuses this same factory for its own "no source row in the
    // group" case — NCLMetaQueryColumn is also an INavValueMetadata, so the exact same call
    // works unchanged. Kept here rather than duplicated, per the sister-drift concern noted
    // elsewhere in this codebase (ComputeJoinColumnSlotMap's comment).
    private static MethodInfo? _mFlowFieldGetDefaultNavValue;
    internal static NavValue? TypedDefaultForField(object fieldObj)
    {
        try
        {
            var nclAsm = fieldObj.GetType().Assembly;
            var tNavValue = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.NavValue");
            var tMeta = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.INavValueMetadata");
            if (tNavValue == null || tMeta == null) return null;
            _mFlowFieldGetDefaultNavValue ??= tNavValue.GetMethod("GetDefaultNavValue",
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static,
                binder: null, types: new[] { tMeta, typeof(bool) }, modifiers: null);
            if (_mFlowFieldGetDefaultNavValue == null) return null;
            return _mFlowFieldGetDefaultNavValue.Invoke(null, new object?[] { fieldObj, false }) as NavValue;
        }
        catch
        {
            return null;
        }
    }

    // NavValuesEqual is gone with #1716: field-to-field where-conditions are no longer
    // compared here at all. BC resolves every condition into a FilterExpression and its own
    // RecordBufferEvaluatorVisitor decides which rows match, so the hand-rolled
    // Equals/decimal/ToString ladder that used to stand in for BC's comparison semantics has
    // no callers — and no chance of disagreeing with them.
    //
    // internal (not private): reused by RecordPatches.QueryProjection.cs's query-column
    // Min/Max aggregation (issue #2137) for the exact same "which of these NavValues wins"
    // comparison, rather than re-deriving comparison semantics a second time.

    internal static int NavValueCompare(NavValue a, NavValue b)
    {
        try
        {
            if (a is IComparable ca && b.GetType() == a.GetType()) return ca.CompareTo(b);
            // Numeric path
            return ((decimal)a.ToDecimal()).CompareTo((decimal)b.ToDecimal());
        }
        catch
        {
            return string.Compare(a.ToString(), b.ToString(), StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Applies BC's own <c>NCLMetaCalculationFormula.NegateValue</c> — the leading minus in
    /// <c>CalcFormula = -sum(...)</c> (#1708). BC's method switches on the SOURCE field's type,
    /// not the value's, which is why exist FlowFields must never be routed through it (#2323);
    /// the callers make that call, not this helper.
    /// <para>internal (not private): shared with
    /// <c>RecordPatches.TempTableDataProvider_CalcNumeric</c> (#2937), which negates at the same
    /// point BC's own provider does — NavSqlAggregateCommand's aggregate reader negates every
    /// aggregated FlowField value whose formula has NegateResult, inside the provider, before
    /// the FieldDictionary goes back to FlowFieldsHelper. One owner rather than two copies of
    /// the "how is a signed FlowField negated" answer.</para>
    /// </summary>
    /// <param name="formula">the field's <c>NCLMetaCalculationFormula</c></param>
    /// <param name="value">the aggregate as computed, unsigned</param>
    /// <param name="surface">the calling surface, for the not-yet-implemented message</param>
    internal static NavValue NegateAggregateResult(object formula, NavValue value, string surface)
    {
        if (_mCalcFormulaNegateValue == null)
            // Writing the POSITIVE aggregate instead would be the exact silent
            // wrong value #1708 is about, so this is loud rather than best-effort.
            AlRunner.Infrastructure.RunnerScope.ThrowNotYetImplemented(
                $"{surface} — CalcFormula = -sum(...) (NCLMetaCalculationFormula.NegateValue)",
                "BC's own value negation is not present on this build, so a signed " +
                "FlowField cannot be computed faithfully — issue #1708");
        return (NavValue)_mCalcFormulaNegateValue!.Invoke(formula, new object?[] { value })!;
    }

    private static NavValue? ReadBufferFieldValue(object buffer, PropertyInfo bufferIndexer, int columnIndex, NCLMetaField? fieldMeta)
    {
        try
        {
            var changed = _mMutableBufferGetChangedFieldValue?.Invoke(buffer, new object[] { columnIndex }) as NavValue;
            if (changed != null) return changed;
        }
        catch
        {
            // Best-effort fallback to original/indexer reads below.
        }

        try
        {
            var original = _mMutableBufferGetOriginalValue?.Invoke(buffer, new object[] { columnIndex }) as NavValue;
            if (original != null) return original;
        }
        catch
        {
            // Best-effort fallback to indexer read below.
        }

        try
        {
            var raw = bufferIndexer.GetValue(buffer, new object[] { columnIndex });
            if (raw is NavValue navValue)
                return navValue;
            if (raw != null && fieldMeta != null)
                return NavValue.CreateNavValueFromObject(fieldMeta, raw);
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static object CoerceSumResult(decimal value)
    {
        if (decimal.Truncate(value) == value)
        {
            if (value >= int.MinValue && value <= int.MaxValue)
                return (int)value;
            if (value >= long.MinValue && value <= long.MaxValue)
                return (long)value;
        }
        return value;
    }
}
