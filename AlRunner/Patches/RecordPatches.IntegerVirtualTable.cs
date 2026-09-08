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
//   So the rows are materialised PER REQUEST, the way Date's are (#2648): a filter closed
//   at both ends materialises exactly the span it names, clamped to BC's [-1e9..1e9]. A base
//   window [IntegerWindowMin .. IntegerWindowMax] is materialised at handout and is what
//   answers a request that does NOT close both ends -- an unbounded filter, a shape we cannot
//   read -- because such a request is answered FROM the window and no narrower store returns
//   the same rows.
//
//   Two refusals remain, and both are limits of materialising rather than statements about BC:
//   a span that would push the store past AL_RUNNER_INTEGER_WINDOW_MAX_ROWS, and a half-open
//   filter whose closed end lies outside the span the base window can answer from, where the
//   rows BC would return are unbounded in number and answering with none would be the silent
//   wrong answer this file exists to remove.
//   See docs/limitations.md#integer-virtual-table.
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
    /// The BASE window: the span materialised at handout, and the span a request that does not
    /// close both of its bounds is answered from. Rows outside it are materialised on demand by
    /// <see cref="PopulateIntegerSpan"/>, so this is a floor rather than a limit.
    ///
    /// <para>Both edges are overridable, and only ever widen. They are no longer the boundary a
    /// closed-bound request is judged against -- <see cref="IntegerWindowMaxRows"/> is -- but they
    /// still decide what an OPEN bound is answered from, which is the one shape no store can
    /// materialise its way out of.</para>
    /// </summary>
    internal const int IntegerWindowMinDefault = -1000;
    internal const int IntegerWindowMaxDefault = 100000;

    /// <summary>
    /// BC's own clamp, from <c>IntegerDataProvider.GetValuesWithinRangeForKeyField</c> and
    /// <c>CountValuesWithinRange</c>: <c>range.GetInclusiveIntegerBounds(-1000000000, 1000000000,
    /// ...)</c>, decompiled from Ncl.dll and byte-identical in 27.0 and 28.4. A bound outside it
    /// names rows a service tier does not serve either, so the span is clamped here exactly as BC
    /// clamps it rather than refused.
    /// </summary>
    internal const int IntegerBcRangeMin = -1_000_000_000;
    internal const int IntegerBcRangeMax = 1_000_000_000;

    /// <summary>
    /// Cap on how many rows this table may materialise, checked before an extension is attempted
    /// so the refusal names a number instead of running out of memory. Same shape and default as
    /// the Date table's (#2648). Overridable with AL_RUNNER_INTEGER_WINDOW_MAX_ROWS.
    /// </summary>
    internal const int IntegerWindowMaxRowsDefault = 500_000;

    private static int? _ivtWindowMaxRows;

    internal static int IntegerWindowMaxRows
    {
        get
        {
            if (_ivtWindowMaxRows.HasValue) return _ivtWindowMaxRows.Value;
            var raw = Environment.GetEnvironmentVariable("AL_RUNNER_INTEGER_WINDOW_MAX_ROWS");
            _ivtWindowMaxRows = int.TryParse(raw, out var v) && v > 0 ? v : IntegerWindowMaxRowsDefault;
            return _ivtWindowMaxRows.Value;
        }
    }

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

    /// <summary>
    /// What each in-memory provider already holds, as disjoint sorted spans of Number plus their
    /// row count. Rows are materialised per request, so "does this provider already hold that
    /// span" is the question every path asks, and the count is what the row cap is checked
    /// against.
    /// </summary>
    private sealed class IntegerPopulatedSpan
    {
        internal readonly List<(int Low, int High)> Covered = new();
        internal long CoveredCount;
    }

    private static readonly ConditionalWeakTable<object, IntegerPopulatedSpan> _ivtSpanByProvider = new();

    /// <summary>True if <paramref name="table"/> is the Integer system virtual table (2000000026).</summary>
    private static bool IsIntegerVirtualTable(NCLMetaTable? table)
        => table != null && table.TableId == IntegerVirtualTableId;

    /// <summary>
    /// Materialise the base window into the store behind this data access. Called at handout;
    /// idempotent, and after the first call it is a span lookup and a return.
    /// </summary>
    private static void PopulateIntegerVirtualTable(object dataAccess, NCLMetaTable integerMetaTable)
        => PopulateIntegerSpan(dataAccess, integerMetaTable, IntegerWindowMin, IntegerWindowMax);

    /// <summary>
    /// Materialise every Number in [<paramref name="wantLow"/>..<paramref name="wantHigh"/>] this
    /// provider does not already hold, clamped to BC's own [-1e9..1e9], and record it as covered.
    /// Refuses -- loudly -- to grow the store past <see cref="IntegerWindowMaxRows"/>.
    /// </summary>
    private static void PopulateIntegerSpan(
        object dataAccess, NCLMetaTable integerMetaTable, int wantLow, int wantHigh)
    {
        EnsureIntegerReflection(integerMetaTable);
        EnsureDataAccessProviderReflection(dataAccess);

        var provider = _pDataAccessDataProvider!.GetValue(dataAccess)
            ?? throw IntegerShapeGap("Integer data access has no in-memory provider");

        // BC clamps rather than refuses, so a bound outside its range names rows that do not
        // exist on a service tier either: clamping here reproduces its answer instead of
        // inventing one. An inverted span selects nothing, so it materialises nothing.
        if (wantLow < IntegerBcRangeMin) wantLow = IntegerBcRangeMin;
        if (wantHigh > IntegerBcRangeMax) wantHigh = IntegerBcRangeMax;
        if (wantHigh < wantLow) return;

        var span = _ivtSpanByProvider.GetValue(provider, static _ => new IntegerPopulatedSpan());

        lock (span)
        {
            var missing = IntegerMissingSpans(span.Covered, wantLow, wantHigh);
            if (missing.Count == 0) return;

            // Checked against what is already there PLUS what this request adds, never against the
            // envelope -- otherwise a narrow request is refused purely because an earlier one sat
            // far away.
            long adding = 0;
            foreach (var (lo0, hi0) in missing) adding += (long)hi0 - lo0 + 1;
            var total = span.CoveredCount + adding;
            if (total > IntegerWindowMaxRows)
                throw IntegerRowCapRefusal(wantLow, wantHigh, adding, total, span);

            // Same rationale as the Field table: make our metatable report IsVirtualTable=false
            // so BC's find takes the NORMAL temp-table DataAccess path over our populated store.
            ClearVirtualBit(integerMetaTable);

            var numberFieldNo = EnsureIntegerNumberFieldNo(integerMetaTable);
            foreach (var (lo0, hi0) in missing)
                for (int n = lo0; n <= hi0; n++)
                    InsertIntegerRow(provider, integerMetaTable, numberFieldNo, n);

            IntegerAddCovered(span.Covered, wantLow, wantHigh);
            span.CoveredCount = total;
        }
    }

    /// <summary>
    /// The refusal for a span that would push the store past the row cap: the span asked for, what
    /// it would add, the resulting total and the cap. A limit of materialising rows, never a claim
    /// that BC cannot answer the request -- which is why it says what BC serves.
    /// </summary>
    private static RunnerOutOfScopeException IntegerRowCapRefusal(
        int wantLow, int wantHigh, long adding, long total, IntegerPopulatedSpan span)
        => IntegerShapeGap(
            System.FormattableString.Invariant(
                $"an Integer filter asks for Number in [{wantLow}..{wantHigh}], which would add ")
            + System.FormattableString.Invariant(
                $"{adding:N0} rows for {total:N0} in all, past the {IntegerWindowMaxRows:N0}-row cap ")
            + "for the materialised table "
            + (span.Covered.Count > 0
                ? System.FormattableString.Invariant(
                      $"(currently {span.CoveredCount:N0} rows in {span.Covered.Count} span(s), ")
                  + System.FormattableString.Invariant(
                      $"[{span.Covered[0].Low}..{span.Covered[^1].High}]). ")
                : "(nothing is materialised yet -- Integer rows are materialised per request). ")
            + "Real BC computes this table per request and serves [-1000000000..1000000000], so "
            + "these are rows a service tier would have returned. "
            + "Raise AL_RUNNER_INTEGER_WINDOW_MAX_ROWS, or narrow the filter");

    /// <summary>
    /// The sub-spans of [<paramref name="wantLow"/>..<paramref name="wantHigh"/>] that
    /// <paramref name="covered"/> does not already hold, in order. <paramref name="covered"/> must
    /// be disjoint and sorted by Low, which <see cref="IntegerAddCovered"/> maintains.
    /// </summary>
    internal static List<(int Low, int High)> IntegerMissingSpans(
        IReadOnlyList<(int Low, int High)> covered, int wantLow, int wantHigh)
    {
        var gaps = new List<(int Low, int High)>();
        if (wantHigh < wantLow) return gaps;

        var cursor = wantLow;
        foreach (var (low, high) in covered)
        {
            if (high < cursor) continue;            // entirely before what is still wanted
            if (low > wantHigh) break;              // sorted, so nothing later can overlap either
            if (low > cursor) gaps.Add((cursor, low - 1));
            if (high >= cursor)
            {
                if (high == int.MaxValue) return gaps;   // nothing above it is left to want
                cursor = high + 1;
            }
            if (cursor > wantHigh) return gaps;
        }
        if (cursor <= wantHigh) gaps.Add((cursor, wantHigh));
        return gaps;
    }

    /// <summary>
    /// Record [<paramref name="low"/>..<paramref name="high"/>] as covered, keeping
    /// <paramref name="covered"/> disjoint, sorted by Low and merged across spans that touch or
    /// overlap -- so a window materialised as two adjacent halves reads as one span rather than as
    /// two with a zero-row gap between them.
    /// </summary>
    internal static void IntegerAddCovered(List<(int Low, int High)> covered, int low, int high)
    {
        if (high < low) return;

        var i = 0;
        while (i < covered.Count && covered[i].High != int.MaxValue && covered[i].High + 1 < low) i++;

        var newLow = low;
        var newHigh = high;
        while (i < covered.Count
               && (covered[i].Low == int.MinValue || covered[i].Low - 1 <= newHigh))
        {
            if (covered[i].Low < newLow) newLow = covered[i].Low;
            if (covered[i].High > newHigh) newHigh = covered[i].High;
            covered.RemoveAt(i);
        }
        covered.Insert(i, (newLow, newHigh));
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
    // Each one WIDENS the materialised set to cover the request, exactly as Date's does, and
    // refuses only when it cannot: past the row cap, or where a half-open filter's closed end
    // lies outside the span an open bound is answered from.

    /// <summary>
    /// Materialise whatever this request needs before it is answered, and refuse when that
    /// cannot be done.
    ///
    /// <para>WHICH SPAN. Every non-empty range of the "Number" filter closed at BOTH ends means
    /// the rows in [lowest low .. highest high] are the only rows the filter can select, so those
    /// are the only rows materialised -- BC's own filter engine excludes everything outside them
    /// regardless of what the store holds. Anything else -- no "Number" filter, an OPEN bound, a
    /// shape ToRangeList cannot express, a request we cannot read -- is answered from the BASE
    /// window, widened by whichever bound IS closed. An open bound is the one shape a materialising
    /// provider cannot follow: BC substitutes its own -1e9 / +1e9 for it
    /// (<c>Range.GetInclusiveIntegerBounds</c>), and 2,000,000,001 rows is not on the table.</para>
    ///
    /// <para>THE HALF-OPEN REFUSAL, decided per RANGE (#3471). When a range's closed end falls outside the span the base
    /// window can answer from -- <c>SetFilter(Number, '>=249000')</c> -- there is no honest span to
    /// materialise: BC returns 249000..1e9 and we would return nothing while reporting success.
    /// That silent zero is refused instead. It is a new refusal, replacing a wrong answer rather
    /// than a working read.</para>
    /// </summary>
    internal static void EnsureIntegerWindowCoversRequest(object dataAccess, object cacheRequest)
    {
        // A `Record Integer temporary` holds exactly the rows AL inserted, and materialising into
        // its private store would inject rows AL never wrote. Same carve-out as Date's (#2524).
        if (IsTemporaryRecordDataAccess(dataAccess)) return;

        NCLMetaTable meta;
        int numberFieldNo;
        try
        {
            if (_pReqMaoLight?.GetValue(cacheRequest) is not NCLMetaTable m) return;
            meta = m;
            EnsureIntegerReflection(meta);
            EnsureIntegerGuardReflection(dataAccess, cacheRequest);
            numberFieldNo = EnsureIntegerNumberFieldNo(meta);
        }
        catch (RunnerOutOfScopeException) { throw; }
        catch
        {
            // We could not identify the request or the store behind it, so there is nothing to
            // materialise against and no answer to protect.
            return;
        }

        int? closedLow, closedHigh;
        bool fullyBounded;
        List<(int Value, bool OpenHigh)> halfOpenEnds;
        try
        {
            fullyBounded = TryReadClosedNumberBounds(
                cacheRequest, dataAccess, numberFieldNo, out closedLow, out closedHigh, out halfOpenEnds);
        }
        catch (RunnerOutOfScopeException) { throw; }
        catch
        {
            // Reading the filter is best-effort. A shape we cannot parse falls back to the base
            // window -- the widest thing such a request can be answered from -- never to a
            // narrower store.
            fullyBounded = false;
            closedLow = closedHigh = null;
            halfOpenEnds = new List<(int, bool)>();
        }

        if (fullyBounded && closedLow is int lo && closedHigh is int hi)
        {
            PopulateIntegerSpan(dataAccess, meta, lo, hi);
            return;
        }

        // The base window, WIDENED by whichever bound the filter did close. Both sides are clamped
        // to the window rather than substituted for it, because a range list like `'..%1|%2..'`
        // closes a HIGH bound on its first range and a LOW bound on its second: taking those as the
        // span would invert it and materialise nothing.
        var lowBound = closedLow is int cl && cl < IntegerWindowMin ? cl : IntegerWindowMin;
        var highBound = closedHigh is int ch && ch > IntegerWindowMax ? ch : IntegerWindowMax;

        // The closed end of a half-open range sits outside the span we are about to materialise, so
        // that range contributes no rows at all while BC answers it with up to a billion. Nothing
        // here can be materialised honestly; say so.
        //
        // Decided per RANGE, never from the envelope (#3471): `'1..50|200000..'` has its outermost
        // closed bounds at 1 and 50, both inside the window, and dropping the second range whole
        // is the same silent zero this refusal exists to remove.
        foreach (var (value, openHigh) in halfOpenEnds)
        {
            if (openHigh && value > highBound) throw IntegerOpenEndedRefusal(value, openHigh: true);
            if (!openHigh && value < lowBound) throw IntegerOpenEndedRefusal(value, openHigh: false);
        }

        PopulateIntegerSpan(dataAccess, meta, lowBound, highBound);
    }

    /// <summary>
    /// The refusal for a half-open filter whose closed end lies outside the base window. Names the
    /// bound, the window it fell outside, and what BC would have answered -- so the reader can
    /// choose between closing the other end of the filter (which is then materialised exactly) and
    /// widening the window.
    /// </summary>
    private static RunnerOutOfScopeException IntegerOpenEndedRefusal(int requested, bool openHigh)
        => IntegerShapeGap(
            System.FormattableString.Invariant(
                $"an Integer filter names Number {requested} with its other end open, outside the ")
            + System.FormattableString.Invariant(
                $"base window [{IntegerWindowMin}..{IntegerWindowMax}] an open bound is answered ")
            + "from. Real BC substitutes "
            + (openHigh ? "1000000000 for the open end" : "-1000000000 for the open end")
            + ", so it answers with rows this request would otherwise be told there are none of. "
            + "Close the other end of the filter, or "
            + (openHigh
                ? "raise AL_RUNNER_INTEGER_WINDOW_MAX"
                : "lower AL_RUNNER_INTEGER_WINDOW_MIN"));

    /// <summary>
    /// The lowest closed low bound and the highest closed high bound this request's "Number"
    /// filter names, read through BC's own <c>FilterExpression.ToRangeList</c>. An open bound
    /// contributes nothing, exactly as <c>GetInclusiveIntegerBounds</c> treats it.
    /// </summary>
    /// <returns>
    /// True only when the filter names at least one non-empty range AND every non-empty range is
    /// closed at both ends, i.e. when [low..high] provably contains every row the filter can
    /// select. False means the request reaches past any bounded span, so the caller answers it
    /// from the base window instead.
    /// </returns>
    private static bool TryReadClosedNumberBounds(
        object cacheRequest, object dataAccess, int numberFieldNo, out int? low, out int? high,
        out List<(int Value, bool OpenHigh)> halfOpenEnds)
    {
        low = null;
        high = null;
        halfOpenEnds = new List<(int, bool)>();

        if (_pFiltersAndMarks!.GetValue(cacheRequest) is not object fam) return false;
        var filter = NumberFilterIn(fam, numberFieldNo);
        if (filter == null) return false;
        if (_ivtDaSession!.GetValue(dataAccess) is not object session) return false;

        var rangeList = _ivtToRangeList!.Invoke(filter, new[] { session });
        if (rangeList == null) return false;
        if (_ivtRangeListRanges!.GetValue(rangeList) is not System.Collections.IEnumerable ranges) return false;

        var sawRange = false;
        var allClosed = true;

        foreach (var range in ranges)
        {
            if (range == null) continue;
            if ((bool)_ivtRangeIsEmpty!.GetValue(range)!) continue;
            sawRange = true;

            // IsLowIsMinimum / IsHighMaximum are the same two flags BC's own
            // GetInclusiveIntegerBounds branches on before substituting its own limits.
            int? rangeLow = null, rangeHigh = null;

            if (!(bool)_ivtRangeLowIsMin!.GetValue(range)!
                && ToInt32OrNull(_ivtRangeLowValue!.GetValue(range)) is int lo)
            {
                rangeLow = lo;
                low = low == null || lo < low ? lo : low;
            }
            else
                allClosed = false;

            if (!(bool)_ivtRangeHighIsMax!.GetValue(range)!
                && ToInt32OrNull(_ivtRangeHighValue!.GetValue(range)) is int hi)
            {
                rangeHigh = hi;
                high = high == null || hi > high ? hi : high;
            }
            else
                allClosed = false;

            // Exactly one end closed: BC substitutes its own limit for the other, so THIS range
            // reaches past anything we can materialise, and its closed end is what decides whether
            // the span we do materialise can answer it at all. Recorded per range, because the
            // envelope loses it whenever another range names a more extreme bound (#3471).
            if (rangeLow is int openHighEnd && rangeHigh == null)
                halfOpenEnds.Add((openHighEnd, true));
            else if (rangeHigh is int openLowEnd && rangeLow == null)
                halfOpenEnds.Add((openLowEnd, false));
        }

        return sawRange && allClosed && low != null && high != null;
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
    /// builds a CountCacheRequest, not a FindCacheRequest, so the find guard never sees it --
    /// without this, a Count over [1..250000] answers whatever the store happens to hold, a number
    /// that looks entirely real. For every table but 2000000026 this is one integer comparison and
    /// a return.
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
    /// TRUE on a range Count() answered 7 for. Without this guard IsEmpty() over [249000..250000]
    /// answers TRUE from a store that holds none of those rows -- a statement about what has been
    /// materialised, dressed as one about the table.
    /// </summary>
    public static void DataAccess_IntegerWindowGuardForExists(object self, object request)
    {
        if (FindRequestTableId(request) != IntegerVirtualTableId) return;
        EnsureIntegerWindowCoversRequest(self, request);
    }

    /// <summary>
    /// Prepended to DataAccess.InternalTryGetByPrimaryKeyAsync for every table. A full-primary-key
    /// <c>Record.Get()</c> reaches neither the find path nor the count path -- DataAccess has its
    /// own primary-key route straight to the provider, which is why #2504 needed a separate guard
    /// there for Aggregate Permission Set and #2648 another for Date. Integer was left behind by
    /// both: <c>Get(250000)</c> answered FALSE, which reads as "no such row" when a service tier
    /// plainly has one.
    ///
    /// <para>The row comes from the RECORD ID rather than from a filter, because a keyed Get
    /// carries its key there and may carry no "Number" filter at all. One row is all it needs, so
    /// this path never reaches the cap; a key outside BC's own [-1e9..1e9] materialises nothing and
    /// falls through to FALSE, which is what a service tier answers for it too.</para>
    /// </summary>
    public static void DataAccess_IntegerWindowGuardForGet(object self, object request)
    {
        if (FindRequestTableId(request) != IntegerVirtualTableId) return;
        if (IsTemporaryRecordDataAccess(self)) return;

        NCLMetaTable meta;
        int? wanted;
        bool hasRecordId;
        try
        {
            if (_pReqMaoLight?.GetValue(request) is not NCLMetaTable m) return;
            meta = m;
            EnsureIntegerReflection(meta);
            EnsureIntegerGuardReflection(self, request);
            wanted = PrimaryKeyNumber(request, out hasRecordId);
        }
        catch (RunnerOutOfScopeException) { throw; }
        catch
        {
            // Unreadable request -- nothing to materialise against.
            return;
        }

        if (wanted is int n)
        {
            PopulateIntegerSpan(self, meta, n, n);
            return;
        }

        // A SystemId-keyed Get names no Number, and cannot name one the store does not already
        // hold: the SystemId of a row that was never materialised has never been handed out.
        if (!hasRecordId) return;

        // A primary-key Get whose key we could not read: answer it from the base window, which is
        // what every Get was answered from before rows became per-request.
        PopulateIntegerSpan(self, meta, IntegerWindowMin, IntegerWindowMax);
    }
}
