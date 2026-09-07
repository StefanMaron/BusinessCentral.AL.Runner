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
//   Real BC is bounded too, just far more widely than we can materialise.
//   IntegerDataProvider.GetValuesWithinRangeForKeyField, decompiled from Ncl.dll and
//   byte-identical in 27.0 and 28.4, opens with
//
//       if (range.GetInclusiveIntegerBounds(-1000000000, 1000000000, out var low, out var high))
//
//   and CountValuesWithinRange applies the same two constants. So a service tier clamps
//   every request to [-1e9 .. 1e9] and serves an UNBOUNDED filter as exactly that range;
//   it never refuses one. 2,000,000,001 rows is not something we can materialise.
//
//   We materialise [IntegerWindowMin .. IntegerWindowMax] and — this is the load-bearing
//   part — a request whose filter CLOSES a bound past the window THROWS
//   RunnerOutOfScopeException naming the requested bound and the window (see
//   DataAccess_IntegerWindowGuardFor* below). Answering a larger request with fewer rows
//   would reproduce, one level up, the exact silent wrong answer this file exists to remove.
//
//   An UNBOUNDED filter is served from the window instead of refused, because that is the
//   shape BC itself answers by inventing a bound, and because BaseApp leans on it: 18 of its
//   658 reports drive a `dataitem(x; Integer)` with no upper bound and stop via MaxIteration
//   (#3374). Refusing there would turn working reports into hard failures while diverging
//   from the service tier. The residual divergence — 101,001 rows where BC yields
//   2,000,000,001 — is real and is recorded in docs/limitations.md, not papered over.
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
    /// <summary>
    /// Every refusal in this file, built in one place. See
    /// RecordPatches.VirtualTableShapeGap.cs for the three-bucket classification and for
    /// why the anchor is "not-yet-implemented" rather than a docs/scope.md section (#2945).
    /// </summary>
    /// <remarks>
    /// Category (2): the runner materialises this table, so the single refusal is a store-
    /// wiring gap and never a statement that Integer is out of scope.
    /// </remarks>
    internal static RunnerOutOfScopeException IntegerShapeGap(string detail)
        => VirtualTableShapeGap("Integer (virtual table 2000000026)", "integer-virtual-table", detail);

    internal const int IntegerVirtualTableId = 2000000026;

    /// <summary>
    /// Materialised window of Number. Real BC serves [-1e9..1e9] computed per request; we
    /// cannot materialise 2,000,000,001 rows. Chosen to cover every realistic synthetic-dataset
    /// use (report row generators, loop drivers) with headroom, while staying cheap enough to
    /// insert eagerly. A request that CLOSES a bound beyond the window throws rather than
    /// returning a short answer.
    ///
    /// <para>Both edges are overridable, and symmetrically so. The lower edge was a hard
    /// <c>const</c> while the upper one already read an environment variable — invisible while
    /// nothing compared a request against either edge, and a dead end the moment #2350's guard
    /// started refusing: a filter naming -250000 was refused with a message advising the reader
    /// to raise <c>AL_RUNNER_INTEGER_WINDOW_MAX</c>, which could not widen the edge that had
    /// actually refused it. Measured: with <c>AL_RUNNER_INTEGER_WINDOW_MAX=300000</c> the
    /// upper-range corpus test passed and the lower-range one still failed.</para>
    /// </summary>
    internal const int IntegerWindowMinDefault = -1000;
    internal const int IntegerWindowMaxDefault = 100000;

    private static int? _ivtWindowMin;
    private static int? _ivtWindowMax;

    internal static int IntegerWindowMin
    {
        get
        {
            if (_ivtWindowMin.HasValue) return _ivtWindowMin.Value;
            var raw = Environment.GetEnvironmentVariable("AL_RUNNER_INTEGER_WINDOW_MIN");
            // Only a value that WIDENS the window is honoured: a "min" above the default would
            // narrow the materialised set and start refusing reads that work today.
            _ivtWindowMin = int.TryParse(raw, out var v) && v < IntegerWindowMinDefault
                ? v : IntegerWindowMinDefault;
            return _ivtWindowMin.Value;
        }
    }

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
            ?? throw IntegerShapeGap("Integer data access has no in-memory provider");

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

    // ── THE WINDOW GUARD ─────────────────────────────────────────────────────────────────
    //
    // Four DataAccess entry points reach this table, and each carries a different request
    // type, so one guard cannot cover them. The set and the reasoning are the Date table's,
    // established over #2648 / #3006 / #2504 and re-used here rather than rediscovered:
    //
    //   find    InnerFindAsync(FindCacheRequest)          — via DataAccess_IsManagedFindRequest
    //   count   CountAsync(CountCacheRequest)             — Record.Count()
    //   exists  ExistsAsync(ExistsCacheRequest)           — Record.IsEmpty(), which never
    //                                                       reaches CountAsync
    //   get     InternalTryGetByPrimaryKeyAsync(...)      — Record.Get(), which reaches
    //                                                       neither find nor count
    //
    // Unlike Date's guard this one never widens anything: the Integer window is materialised
    // eagerly and in full at handout, so there is nothing to top up. The only question a
    // guard can answer here is "does this request reach past what we hold", and the only
    // honest answer when it does is a refusal.

    /// <summary>
    /// Field number of Integer's "Number" column as BC's own metadata declares it. Bound at
    /// populate time from the metatable (<see cref="EnsureIntegerNumberFieldNo"/>) rather than
    /// hardcoded; this cache lets the guard read a filter without re-walking the field list on
    /// every request. Null until the table has been handed out once, which is also the only
    /// state in which there is nothing to protect.
    /// </summary>
    private static int? IntegerNumberFieldNoOrNull => _ivtNumberFieldNo;

    /// <summary>
    /// Refuse when <paramref name="cacheRequest"/>'s "Number" filter closes a bound outside
    /// [<see cref="IntegerWindowMin"/> .. <see cref="IntegerWindowMax"/>].
    ///
    /// <para>THE RULE, and why it is about CLOSED bounds only. BC's own
    /// <c>Range.GetInclusiveIntegerBounds(min, max, ...)</c> substitutes its <c>min</c> for an
    /// open low bound and its <c>max</c> for an open high one, then clamps whatever the filter
    /// did name into that interval. We mirror the first half exactly — an open bound means "as
    /// far as this provider goes", which is the window — and deliberately diverge on the
    /// second: where BC silently clamps a closed bound of 2e9 down to 1e9, we refuse a closed
    /// bound of 250000 rather than clamp it to 100000. Clamping is safe for BC because the
    /// rows between its clamp and the request are rows that do not exist; clamping here would
    /// drop rows a service tier would have returned.</para>
    ///
    /// <para>Every non-empty range in the filter is checked, so <c>'1..50|200000..300000'</c>
    /// is refused on its second range even though its first is comfortably inside. A filter we
    /// cannot read at all falls through to being served: that is the pre-existing behaviour,
    /// and a refusal we cannot justify from a bound we actually read would be a worse failure
    /// than the one this guard removes.</para>
    /// </summary>
    internal static void EnsureIntegerWindowCoversRequest(object dataAccess, object cacheRequest)
    {
        // A `Record Integer temporary` holds exactly the rows AL inserted, and the real table's
        // rows were never injected into its private store. Refusing on its filter would refuse a
        // read that has nothing to do with the virtual table. Same carve-out as Date's (#2524).
        if (IsTemporaryRecordDataAccess(dataAccess)) return;

        // Nothing has been materialised yet, so the "Number" field number is not bound and there
        // is no populated store whose limits could be exceeded. PrepareIntegerVirtualTable
        // populates before any request is served.
        if (IntegerNumberFieldNoOrNull is not int numberFieldNo) return;

        int? closedLow, closedHigh;
        try
        {
            EnsureIntegerGuardReflection(dataAccess, cacheRequest);
            if (!TryReadClosedNumberBounds(cacheRequest, dataAccess, numberFieldNo, out closedLow, out closedHigh))
                return;
        }
        catch (RunnerOutOfScopeException) { throw; }
        catch
        {
            // Reading the filter is best-effort. A shape we cannot parse is served from the
            // window exactly as it was before this guard existed — see the class comment: a
            // refusal must rest on a bound we actually read.
            return;
        }

        if (closedLow is int lo && lo < IntegerWindowMin) throw IntegerWindowRefusal(lo);
        if (closedHigh is int hi && hi > IntegerWindowMax) throw IntegerWindowRefusal(hi);
    }

    /// <summary>
    /// The refusal, naming the bound that was asked for and the window that could not answer
    /// it — the two facts a reader needs to decide between raising
    /// <c>AL_RUNNER_INTEGER_WINDOW_MAX</c> and narrowing the filter.
    /// </summary>
    private static RunnerOutOfScopeException IntegerWindowRefusal(int requested)
        => IntegerShapeGap(
            System.FormattableString.Invariant(
                $"an Integer filter names Number {requested}, past the materialised window ")
            + System.FormattableString.Invariant(
                $"[{IntegerWindowMin}..{IntegerWindowMax}]. ")
            + "Real BC computes this table per request and serves [-1000000000..1000000000], so "
            + "the rows between the window and the bound asked for are rows a service tier would "
            + "have returned. "
            // Name the variable that moves the edge that actually refused. Advising
            // AL_RUNNER_INTEGER_WINDOW_MAX for a bound below the window sends the reader to a
            // setting that cannot widen it, and the advice fails silently.
            + (requested < IntegerWindowMin
                ? "Lower AL_RUNNER_INTEGER_WINDOW_MIN, or narrow the filter"
                : "Raise AL_RUNNER_INTEGER_WINDOW_MAX, or narrow the filter"));

    /// <summary>
    /// The lowest closed low bound and the highest closed high bound this request's "Number"
    /// filter names, read through BC's own <c>FilterExpression.ToRangeList</c>. An open bound
    /// contributes nothing, exactly as <c>GetInclusiveIntegerBounds</c> treats it.
    /// </summary>
    /// <returns>False when there is no readable "Number" filter, so nothing can be judged.</returns>
    private static bool TryReadClosedNumberBounds(
        object cacheRequest, object dataAccess, int numberFieldNo, out int? low, out int? high)
    {
        low = null;
        high = null;

        if (_pFiltersAndMarks!.GetValue(cacheRequest) is not object fam) return false;
        var filter = NumberFilterIn(fam, numberFieldNo);
        if (filter == null) return false;
        if (_ivtDaSession!.GetValue(dataAccess) is not object session) return false;

        var rangeList = _ivtToRangeList!.Invoke(filter, new[] { session });
        if (rangeList == null) return false;
        if (_ivtRangeListRanges!.GetValue(rangeList) is not System.Collections.IEnumerable ranges) return false;

        foreach (var range in ranges)
        {
            if (range == null) continue;
            if ((bool)_ivtRangeIsEmpty!.GetValue(range)!) continue;

            // IsLowIsMinimum / IsHighMaximum are the same two flags BC's own
            // GetInclusiveIntegerBounds branches on before substituting its own limits.
            if (!(bool)_ivtRangeLowIsMin!.GetValue(range)!
                && ToInt32OrNull(_ivtRangeLowValue!.GetValue(range)) is int lo)
                low = low == null || lo < low ? lo : low;

            if (!(bool)_ivtRangeHighIsMax!.GetValue(range)!
                && ToInt32OrNull(_ivtRangeHighValue!.GetValue(range)) is int hi)
                high = high == null || hi > high ? hi : high;
        }

        return low != null || high != null;
    }

    /// <summary>The "Number" FilterExpression inside a <c>FiltersAndMarks</c>, if any.</summary>
    private static object? NumberFilterIn(object fam, int numberFieldNo)
    {
        var filters = _pFamFilters!.GetValue(fam);
        if (filters == null) return null;
        if (_pFfdItems!.GetValue(filters) is not Array items) return null;

        foreach (var item in items)
        {
            if (item == null) continue;
            var tupleType = item.GetType();
            var fieldMeta = tupleType.GetProperty("Item1")?.GetValue(item);
            var expr = tupleType.GetProperty("Item2")?.GetValue(item);
            if (fieldMeta == null || expr == null) continue;
            if (_pFieldNo?.GetValue(fieldMeta) is int fieldNo && fieldNo == numberFieldNo)
                return expr;
        }
        return null;
    }

    private static int? ToInt32OrNull(object? navValue)
    {
        if (navValue == null) return null;
        try
        {
            return _ivtNavValueToInt32!.Invoke(navValue, null) is int i ? i : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// The primary-key value a keyed <c>Record.Get(Number)</c> names. Integer's primary key is
    /// ("Number") alone, so it is the FIRST and only key value — unlike Date's, where
    /// "Period Start" is the second.
    /// </summary>
    /// <param name="hasRecordId">
    /// False for a SystemIdCacheRequest, which carries no RecordId. A Get by SystemId cannot
    /// name a row outside the window: the SystemId of a row that was never materialised has
    /// never been handed out, so there is nothing to refuse.
    /// </param>
    private static int? PrimaryKeyNumber(object request, out bool hasRecordId)
    {
        hasRecordId = false;
        try
        {
            var recordId = request.GetType().GetProperty("RecordId",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(request);
            if (recordId == null) return null;
            hasRecordId = true;

            var fields = recordId.GetType().GetProperty("Fields",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(recordId);
            if (fields is not System.Collections.IList list || list.Count < 1) return null;

            return ToInt32OrNull(list[0]);
        }
        catch { return null; }
    }

    private static volatile bool _ivtGuardReady;
    private static PropertyInfo? _ivtDaSession;
    private static MethodInfo? _ivtToRangeList;
    private static PropertyInfo? _ivtRangeListRanges;
    private static PropertyInfo? _ivtRangeLowIsMin, _ivtRangeHighIsMax;
    private static PropertyInfo? _ivtRangeLowValue, _ivtRangeHighValue, _ivtRangeIsEmpty;
    private static MethodInfo? _ivtNavValueToInt32;

    /// <summary>
    /// Bind the filter-reading reflection this guard needs, with a hard throw when a member is
    /// genuinely absent — the same shape as <see cref="EnsureIntegerReflection"/>, and for the
    /// same reason: a silent bind failure here would turn the guard back into the no-op this
    /// file's header spent a release claiming it was not.
    /// </summary>
    private static void EnsureIntegerGuardReflection(object dataAccess, object cacheRequest)
    {
        if (_ivtGuardReady) return;

        // Shares the FiltersAndMarks / FilterFieldDictionary accessors with the Field-table find
        // interception; EnsureFilterReflection binds those.
        EnsureFilterReflection(cacheRequest);

        var nclAsm = cacheRequest.GetType().Assembly;
        const string rt = "Microsoft.Dynamics.Nav.Runtime.";
        const BindingFlags anyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        var tCacheRequest = nclAsm.GetType(rt + "DataCacheRequest")
            ?? throw new InvalidOperationException("DataCacheRequest not found — BC metadata shape changed");
        _pFiltersAndMarks ??= tCacheRequest.GetProperty("FiltersAndMarks", anyInstance)
            ?? throw new InvalidOperationException("DataCacheRequest.FiltersAndMarks not found — BC metadata shape changed");

        var tDataAccess = nclAsm.GetType(rt + "DataAccess")
            ?? throw new InvalidOperationException("DataAccess not found — BC metadata shape changed");
        _ivtDaSession = tDataAccess.GetProperty("Session", anyInstance)
            ?? tDataAccess.GetProperty("session", anyInstance)
            ?? throw new InvalidOperationException("DataAccess.Session not found — BC metadata shape changed");

        var tFilterExpr = nclAsm.GetType(rt + "FilterExpression")
            ?? throw new InvalidOperationException("FilterExpression not found — BC metadata shape changed");
        _ivtToRangeList = tFilterExpr.GetMethods(anyInstance)
            .FirstOrDefault(m => m.Name == "ToRangeList" && m.GetParameters().Length == 1)
            ?? throw new InvalidOperationException("FilterExpression.ToRangeList(1 arg) not found — BC metadata shape changed");

        var tRangeList = nclAsm.GetType(rt + "RangeList")
            ?? throw new InvalidOperationException("RangeList not found — BC metadata shape changed");
        _ivtRangeListRanges = tRangeList.GetProperty("Ranges", anyInstance)
            ?? throw new InvalidOperationException("RangeList.Ranges not found — BC metadata shape changed");

        var tRange = nclAsm.GetType(rt + "Range")
            ?? throw new InvalidOperationException("Range not found — BC metadata shape changed");
        PropertyInfo NeedProp(string name) => tRange.GetProperty(name, anyInstance)
            ?? throw new InvalidOperationException($"Range.{name} not found — BC metadata shape changed");
        _ivtRangeLowIsMin = NeedProp("IsLowIsMinimum");
        _ivtRangeHighIsMax = NeedProp("IsHighMaximum");
        _ivtRangeLowValue = NeedProp("LowValue");
        _ivtRangeHighValue = NeedProp("HighValue");
        _ivtRangeIsEmpty = NeedProp("IsEmptyRange");

        var tNavValue = ResolveType(rt + "NavValue", "Microsoft.Dynamics.Nav.Types.NavValue")
            ?? throw new InvalidOperationException("NavValue type not found — BC metadata shape changed");
        _ivtNavValueToInt32 = tNavValue.GetMethod("ToInt32",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null, types: Type.EmptyTypes, modifiers: null)
            ?? throw new InvalidOperationException("NavValue.ToInt32() not found — BC metadata shape changed");

        _ivtGuardReady = true;
    }

    /// <summary>
    /// Prepended to DataAccess.CountAsync(CountCacheRequest) for every table. <c>Record.Count()</c>
    /// builds a CountCacheRequest, not a FindCacheRequest, so the find guard never sees it —
    /// without this, a Count over [1..250000] answers 100000, a number that looks entirely real
    /// and is short by 150000. For every table but 2000000026 this is one integer comparison
    /// and a return.
    /// </summary>
    public static void DataAccess_IntegerWindowGuardForCount(object self, object request)
    {
        if (FindRequestTableId(request) != IntegerVirtualTableId) return;
        EnsureIntegerWindowCoversRequest(self, request);
    }

    /// <summary>
    /// Prepended to DataAccess.ExistsAsync(ExistsCacheRequest) for every table.
    /// <c>Record.IsEmpty()</c> does not take the count path: RecordImplementation.IsEmptyAsync
    /// builds its own ExistsCacheRequest and reaches DataAccess.ExistsAsync, never CountAsync —
    /// established for the Date table in #3006, where the same omission had IsEmpty() answering
    /// TRUE on a range Count() answered 7 for. Without this guard IsEmpty() over [1..250000]
    /// answers FALSE from the rows the window happens to hold: true by accident, and a statement
    /// about a range nobody asked about.
    /// </summary>
    public static void DataAccess_IntegerWindowGuardForExists(object self, object request)
    {
        if (FindRequestTableId(request) != IntegerVirtualTableId) return;
        EnsureIntegerWindowCoversRequest(self, request);
    }

    /// <summary>
    /// Prepended to DataAccess.InternalTryGetByPrimaryKeyAsync for every table. A full-primary-key
    /// <c>Record.Get()</c> reaches neither the find path nor the count path — DataAccess has its
    /// own primary-key route straight to the provider, which is why #2504 needed a separate guard
    /// there for Aggregate Permission Set and #2648 another for Date. Integer was left behind by
    /// both: <c>Get(250000)</c> answered FALSE, which reads as "no such row" when a service tier
    /// plainly has one.
    ///
    /// <para>The bound comes from the RECORD ID rather than from a filter, because a keyed Get
    /// carries its key there and may carry no "Number" filter at all.</para>
    /// </summary>
    public static void DataAccess_IntegerWindowGuardForGet(object self, object request)
    {
        if (FindRequestTableId(request) != IntegerVirtualTableId) return;
        if (IsTemporaryRecordDataAccess(self)) return;

        int? wanted;
        try
        {
            EnsureIntegerGuardReflection(self, request);
            wanted = PrimaryKeyNumber(request, out _);
        }
        catch (RunnerOutOfScopeException) { throw; }
        catch
        {
            // Unreadable request — served from the window, as before this guard existed.
            return;
        }

        if (wanted is not int n) return;
        if (n < IntegerWindowMin || n > IntegerWindowMax) throw IntegerWindowRefusal(n);
    }
}
