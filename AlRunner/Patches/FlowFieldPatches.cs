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
    private static PropertyInfo? _pCalcFormulaTableId;
    private static PropertyInfo? _pCalcFormulaFieldId;
    private static PropertyInfo? _pFilterSourceField;          // NCLMetaFilter.SourceField
    private static PropertyInfo? _pFilterFieldValueField;      // NCLMetaFilterField.ValueField (returns INavFieldMetadata)
    private static FieldInfo? _fCalcFormulaEmpty;              // NCLMetaCalculationFormula.EmptyFormula

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
        _pCalcFormulaTableId = _tNCLMetaCalcFormula.GetProperty("TableId");
        _pCalcFormulaFieldId = _tNCLMetaCalcFormula.GetProperty("FieldId");
        _fCalcFormulaEmpty = _tNCLMetaCalcFormula.GetField("EmptyFormula",
            BindingFlags.Public | BindingFlags.Static);

        // NCLMetaFilter / NCLMetaFilterField
        _pFilterSourceField = _tNCLMetaFilter.GetProperty("SourceField",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        _pFilterFieldValueField = _tNCLMetaFilterField.GetProperty("ValueField",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        _pNclMetaFilterFilterType = _tNCLMetaFilter.GetProperty("FilterType",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var tFilterTypeEnum = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.NCLMetaFilterType");
        if (tFilterTypeEnum != null)
            _filterTypeField = Enum.Parse(tFilterTypeEnum, "Field");

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

            // The skeleton DAS — needed to obtain source-table TempTableDataProvider
            var dataAccessSource = _fSessionDataAccessSource?.GetValue(session);

            // Buffer write helper via indexer
            var bufferType = parentBuffer.GetType();
            var bufferIndexer = bufferType.GetProperty("Item",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, typeof(NavValue), new[] { typeof(int) }, null)
                ?? bufferType.GetProperty("Item",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (bufferIndexer == null) return new System.Threading.Tasks.ValueTask<bool>(false);

            foreach (var fieldObj in fields)
            {
                if (fieldObj == null) continue;
                var fieldClass = _pNclMetaFieldFieldClass!.GetValue(fieldObj);
                int dbgCol = -1; try { dbgCol = (int)_pNclMetaFieldColumnIndex!.GetValue(fieldObj)!; } catch { }

                // Handle BLOB fields: copy stored content from TempTableDataProvider into mutableRecordBuffer
                if (_pNclMetaFieldFieldNclType != null && _nclTypeNavBlob != null
                    && Equals(_pNclMetaFieldFieldNclType.GetValue(fieldObj), _nclTypeNavBlob))
                {
                    if (dbgCol >= 0)
                        LoadBlobField(self, parentBuffer, bufferIndexer, dbgCol);
                    continue;
                }

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

                // Resolve filters: list of (sourceFieldColumnIndex, parentFieldColumnIndex)
                var filters = _pCalcFormulaFilters!.GetValue(formula) as IEnumerable;
                var filterPairs = new List<(int srcCol, int parentCol, NCLMetaField srcField, NCLMetaField parentField)>();
                if (filters != null)
                {
                    foreach (var fObj in filters)
                    {
                        if (fObj == null) continue;
                        if ((_tNCLMetaFilter != null && !_tNCLMetaFilter.IsInstanceOfType(fObj))
                            && (_tNCLMetaFilterField != null && !_tNCLMetaFilterField.IsInstanceOfType(fObj)))
                            continue;

                        object? fSrc = null;
                        object? fVal = null;
                        try
                        {
                            fSrc = _pFilterSourceField?.GetValue(fObj);
                            fVal = _pFilterFieldValueField?.GetValue(fObj);
                        }
                        catch
                        {
                            continue;
                        }
                        if (fSrc == null || fVal == null) continue;
                        if (fSrc is not NCLMetaField srcFilterField || fVal is not NCLMetaField parentFilterField)
                            continue;

                        int sCol = srcFilterField.ColumnIndex;
                        int pCol = parentFilterField.ColumnIndex;
                        filterPairs.Add((sCol, pCol, srcFilterField, parentFilterField));
                    }

                }

                // Pre-read parent values for each filter
                var parentFilterValues = new NavValue?[filterPairs.Count];
                for (int i = 0; i < filterPairs.Count; i++)
                    parentFilterValues[i] = ReadBufferFieldValue(parentBuffer, bufferIndexer, filterPairs[i].parentCol, filterPairs[i].parentField);

                // Enumerate source rows via TempTableDataProvider.Filter to mirror the runner's
                // CalcNumeric path (company-scoped, key-ordered, current in-memory rows).
                IEnumerable? rows = null;
                try
                {
                    var sortingFields = _fTtdpPrimaryKeySortingFields?.GetValue(srcTtdp);
                    rows = _mTtdpFilter?.Invoke(srcTtdp, new object?[]
                    {
                        companyToken,
                        _emptyFm,
                        null,
                        sortingFields,
                        false
                    }) as IEnumerable;
                }
                catch { }
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
                    bool pass = true;
                    for (int i = 0; i < filterPairs.Count; i++)
                    {
                        var rowVal = ReadBufferFieldValue(row, rowIndexer, filterPairs[i].srcCol, filterPairs[i].srcField);
                        if (!NavValuesEqual(rowVal, parentFilterValues[i])) { pass = false; break; }
                    }
                    if (!pass) continue;

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
                NavValue? result = null;
                int targetColumn = (int)_pNclMetaFieldColumnIndex!.GetValue(fieldObj)!;

                if (Equals(calcMethod, _cmCount))
                    result = NavValue.CreateNavValueFromObject((NCLMetaField)fieldObj, matchCount);
                else if (Equals(calcMethod, _cmExist))
                    result = NavValue.CreateNavValueFromObject((NCLMetaField)fieldObj, anyMatch);
                else if (Equals(calcMethod, _cmSum))
                {
                    var v = negate ? -sum : sum;
                    result = NavValue.CreateNavValueFromObject((NCLMetaField)fieldObj, CoerceSumResult(v));
                }
                else if (Equals(calcMethod, _cmAverage))
                {
                    var v = matchCount > 0 ? sum / matchCount : 0m;
                    if (negate) v = -v;
                    result = NavValue.CreateNavValueFromObject((NCLMetaField)fieldObj, v);
                }
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

                bufferIndexer.SetValue(parentBuffer, result, new object[] { targetColumn });
            }

            return new System.Threading.Tasks.ValueTask<bool>(true);
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            Console.Error.WriteLine($"[FlowFieldPatches] inner ex: {tie.InnerException.GetType().Name}: {tie.InnerException.Message}");
            Console.Error.WriteLine(tie.InnerException.StackTrace ?? "");
            // Rethrow honoring DataError contract
            if (errorLevel == DataError.TrapError)
                return new System.Threading.Tasks.ValueTask<bool>(false);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(tie.InnerException);
            return default;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FlowFieldPatches] ex: {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace ?? "");
            if (errorLevel == DataError.TrapError)
                return new System.Threading.Tasks.ValueTask<bool>(false);
            throw;
        }
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
            // Step 1: ensure a writable NavBLOB is in changedValues (mirrors GetWriteableBlobOnField...)
            var navBLOB = _mMutableBufferGetChangedFieldValue?.Invoke(parentBuffer, new object[] { fieldIdx }) as NavBLOB;
            if (navBLOB == null)
            {
                var original = _mMutableBufferGetOriginalValue?.Invoke(parentBuffer, new object[] { fieldIdx }) as NavBLOB;
                navBLOB = original != null ? new NavBLOB(original.ALLength) : NavBLOB.Default();
                bufferIndexer.SetValue(parentBuffer, navBLOB, new object[] { fieldIdx });
            }

            // Step 2: get the TempTableDataProvider for the current record's table
            var dataAccess = _fRecImplDataAccess?.GetValue(self);
            var dataProvider = dataAccess != null ? _pDataAccessDataProvider?.GetValue(dataAccess) : null;
            if (dataProvider == null || _mTtdpTryGetValue == null || _mMutableBufferGetRecordId == null) return;

            // Step 3: look up the stored TempTableRecordBuffer by primary key
            var recordId = _mMutableBufferGetRecordId.Invoke(parentBuffer, null);
            var tryGetArgs = new object?[] { recordId, null };
            if (_mTtdpTryGetValue.Invoke(dataProvider, tryGetArgs) is not true) return;

            var storedBuffer = tryGetArgs[1];
            if (storedBuffer == null) return;

            // Step 4: copy blob data from stored buffer into the writable NavBLOB
            var storedIndexer = storedBuffer.GetType().GetProperty("Item",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, typeof(NavValue), new[] { typeof(int) }, null);
            var storedBLOB = storedIndexer?.GetValue(storedBuffer, new object[] { fieldIdx }) as NavBLOB;
            if (storedBLOB == null || storedBLOB.IsZeroOrEmpty) return;

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
    private static MethodInfo? _mFlowFieldGetDefaultNavValue;
    private static NavValue? TypedDefaultForField(object fieldObj)
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

    private static bool NavValuesEqual(NavValue? a, NavValue? b)
    {
        if (a == null && b == null) return true;
        if (a == null || b == null) return false;
        // Try Equals; fall back to ToString comparison for safety.
        try
        {
            if (a.Equals(b)) return true;
            // Numeric fast-path: both convertible to decimal
            try { return a.ToDecimal().Equals(b.ToDecimal()); } catch { /* not numeric */ }
            return string.Equals(a.ToString(), b.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static int NavValueCompare(NavValue a, NavValue b)
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
