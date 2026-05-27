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
using AlRunnerV2.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;

namespace AlRunnerV2.Patches;

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
    private static PropertyInfo? _pCalcFormulaSourceField;
    private static PropertyInfo? _pCalcFormulaNegateResult;
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
        _fCalcFormulaEmpty = _tNCLMetaCalcFormula.GetField("EmptyFormula",
            BindingFlags.Public | BindingFlags.Static);

        // NCLMetaFilter / NCLMetaFilterField
        _pFilterSourceField = _tNCLMetaFilter.GetProperty("SourceField",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        _pFilterFieldValueField = _tNCLMetaFilterField.GetProperty("ValueField",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

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

                // Source field/table from formula
                var srcFieldMeta = _pCalcFormulaSourceField!.GetValue(formula); // NCLMetaField (may be null for Count/Exist)
                int srcFieldColumn = -1;
                NCLMetaTable? srcTable = null;
                if (srcFieldMeta != null)
                {
                    srcFieldColumn = (int)_pNclMetaFieldColumnIndex!.GetValue(srcFieldMeta)!;
                    srcTable = _pNclMetaFieldParent!.GetValue(srcFieldMeta) as NCLMetaTable;
                }
                else
                {
                    // Count/Exist: derive source table from formula.TableId via metadata service
                    var tableId = (int)(_tNCLMetaCalcFormula!.GetProperty("TableId")!.GetValue(formula) ?? 0);
                    srcTable = ResolveTableById(tableId);
                }
                // Source TempTableDataProvider — call our replacement directly, NOT via reflection
                // (MethodInfo.Invoke bypasses JmpHook and gets the original empty-store impl).
                if (srcTable == null) continue;
                object? srcTtdp = null;
                try
                {
                    var srcDataAccess = AlRunnerV2.Patches.RecordPatches
                        .NavDataAccessSource_GetDataAccessForTable(dataAccessSource!, srcTable, false);
                    if (srcDataAccess != null && _pDataAccessDataProvider != null)
                        srcTtdp = _pDataAccessDataProvider.GetValue(srcDataAccess) ?? srcDataAccess;
                }
                catch { }
                if (srcTtdp == null) continue;

                // Resolve filters: list of (sourceFieldColumnIndex, parentFieldColumnIndex)
                var filters = _pCalcFormulaFilters!.GetValue(formula) as IEnumerable;
                var filterPairs = new List<(int srcCol, int parentCol)>();
                if (filters != null)
                {
                    foreach (var fObj in filters)
                    {
                        if (fObj == null) continue;
                        if (!_tNCLMetaFilterField!.IsInstanceOfType(fObj)) continue;
                        var fSrc = _pFilterSourceField!.GetValue(fObj);
                        var fVal = _pFilterFieldValueField!.GetValue(fObj);
                        if (fSrc == null || fVal == null) continue;
                        int sCol = (int)_pNclMetaFieldColumnIndex!.GetValue(fSrc)!;
                        int pCol = (int)_pNclMetaFieldColumnIndex!.GetValue(fVal)!;
                        filterPairs.Add((sCol, pCol));
                    }
                }

                // Pre-read parent values for each filter
                var parentFilterValues = new NavValue?[filterPairs.Count];
                for (int i = 0; i < filterPairs.Count; i++)
                    parentFilterValues[i] = bufferIndexer.GetValue(parentBuffer, new object[] { filterPairs[i].parentCol }) as NavValue;

                // Enumerate source rows directly from primaryTree — it IS IEnumerable<TempTableRecordBuffer>.
                // (Avoid TempTableDataProvider.Filter — its result enumerable behaves oddly when invoked
                //  via reflection from outside Ncl assembly; iterating the AvlTree directly is robust.)
                IEnumerable? rows = null;
                try
                {
                    var fPrimaryTree = srcTtdp.GetType().GetField("primaryTree", BindingFlags.NonPublic | BindingFlags.Instance);
                    rows = fPrimaryTree?.GetValue(srcTtdp) as IEnumerable;
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
                        var rowVal = rowIndexer.GetValue(row, new object[] { filterPairs[i].srcCol }) as NavValue;
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
                        var srcVal = rowIndexer.GetValue(row, new object[] { srcFieldColumn }) as NavValue;
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
                    result = NavValue.CreateNavValueFromObject((NCLMetaField)fieldObj, v);
                }
                else if (Equals(calcMethod, _cmAverage))
                {
                    var v = matchCount > 0 ? sum / matchCount : 0m;
                    if (negate) v = -v;
                    result = NavValue.CreateNavValueFromObject((NCLMetaField)fieldObj, v);
                }
                else if (Equals(calcMethod, _cmMin))
                    result = minV ?? NavValue.CreateNavValueFromObject((NCLMetaField)fieldObj, 0);
                else if (Equals(calcMethod, _cmMax))
                    result = maxV ?? NavValue.CreateNavValueFromObject((NCLMetaField)fieldObj, 0);
                else if (Equals(calcMethod, _cmLookup))
                    result = lookupV ?? NavValue.CreateNavValueFromObject((NCLMetaField)fieldObj, "");
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
}
