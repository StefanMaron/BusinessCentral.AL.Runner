// RecordPatches.DateVirtualTable — managed provider for the Date system virtual table
// (2000000007).
//
// WHY THIS EXISTS
//   On the real service tier Date is a VIRTUAL table whose rows are computed on demand by
//   Microsoft.Dynamics.Nav.Runtime.DateDataProvider (a RangeBasedComputedDataProvider):
//   one row per period, for each of the five period types Date / Week / Month / Quarter /
//   Year, keyed on ("Period Type", "Period Start"). There are no stored rows.
//
//   Our runtime routes every table's data access through
//   NavDataAccessSource_GetDataAccessForTable → an in-memory TempTableDataProvider, and for
//   2000000007 that store was empty. So `Record Date` was ALWAYS empty, and any AL that
//   iterates it failed with "There is no Date within the filter."
//
//   That is a mainstream surface, not an exotic one. The Date table is how AL asks the
//   platform which weekday a date is, which ISO week it falls in, and when a month or
//   quarter ends, without doing the arithmetic itself. 29 tests in Microsoft's
//   Tests-SINGLESERVER bucket fail on this one gap (issue #2309).
//
// WHAT THIS DOES (faithful, managed, R2R-safe)
//   We keep the in-memory TempTableDataProvider (so BC's own filter/sort/Find engine applies
//   the AL filters exactly as it does for every other table) and POPULATE it with one row per
//   period over a bounded window. Row values are laid out exactly as BC lays out a virtual
//   record: VirtualDataProvider.GetSystemPopulatedVirtualRecordValues — BC's OWN helper —
//   fills the timestamp / SystemId / audit slots, and each Date column is written at the slot
//   BC's own NCLMetaField.FieldIndex says it occupies.
//
//   EVERY piece of period arithmetic is BC's own code, called by reflection, never
//   re-derived here:
//     DateTimeHelper.DatePeriodRoundUp / DatePeriodRoundDown / IsDateAtStartOfPeriod /
//     DatePeriodStartMinimumDate / DatePeriodStartMaximumDate,
//     DateDataProvider.ToNextPeriodStart / CalculatePeriodNumber / GetPeriodName.
//   So the runner cannot disagree with the service tier about which Monday starts ISO week 4,
//   what a leap February ends on, or what a month is called in the server's language.
//   Decompiled reference: DateDataProvider at Ncl line 127588 (CalculatePeriodNumber 127711,
//   GetPeriodName 127743, ToNextPeriodStart 127835); DateTimeHelper at line 177883.
//
//   "Period End" is a CLOSING date on the real table (DateDataProvider line 127684 builds it
//   with NavDate.CreateDate(result, closing: true)); Base Application code depends on that and
//   calls NormalDate() on it whenever it wants the calendar day. We build it the same way.
//
// THE WINDOW, AND WHAT IT DOES AND DOES NOT PROMISE
//   The real table spans years 1 through 9999 — 3.6 million Date rows alone — so it cannot be
//   materialised whole. We materialise a window of whole years (default 1900-01-01 ..
//   2099-12-31, about 87,000 rows across all five period types) and, at find time, EXTEND that
//   window on demand whenever an AL filter names a closed bound outside it
//   (EnsureDateWindowCoversRequest, called from the InnerFindAsync guard in
//   RecordPatches.FieldFindIntercept.cs). Past AL_RUNNER_DATE_WINDOW_MAX_ROWS the extension
//   throws RunnerOutOfScopeException naming the requested bound, the window and the cap,
//   rather than answering a larger request with fewer rows.
//
//   The one thing the window does NOT cover is an OPEN bound: `SetFilter("Period Start",
//   '%1..', D)` asks BC for everything up to 9999-12-31, and we answer it from the window.
//   An ascending FindFirst — the shape production AL actually uses — is unaffected, because
//   its answer sits at the closed end. A full iteration of an open-ended range stops at the
//   window edge. That limit is documented in docs/limitations.md; it is not silently
//   different per run, and both edges are settable with AL_RUNNER_DATE_WINDOW_MIN_YEAR /
//   AL_RUNNER_DATE_WINDOW_MAX_YEAR.
//
// PRECOMPILED-DLL RESPECT
//   No BC business-logic body is touched. VirtualDataProvider, DateDataProvider,
//   DateTimeHelper, NCLMetaTable, NavValue, ReadOnlyRecordBuffer and TempTableDataProvider are
//   runtime-engine types; we call BC's own helpers by reflection and feed the result into our
//   own in-memory store.
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    internal const int DateVirtualTableId = 2000000007;

    // Field numbers of the Date table, exactly as BC's own DateDataProvider hardcodes them
    // when it lays out a row (Ncl line 127690-127700: buffer slot i carries field i+1) and
    // when it evaluates non-primary-key filters (line 127668: cases 3, 4, 5). We validate the
    // names against the metatable on first use rather than trusting the numbers blindly — see
    // EnsureDateFieldLayout.
    private const int DateFieldPeriodType = 1;
    private const int DateFieldPeriodStart = 2;
    private const int DateFieldPeriodEnd = 3;
    private const int DateFieldPeriodNo = 4;
    private const int DateFieldPeriodName = 5;
    private const int DateFieldPeriodNameInvariant = 6;

    /// <summary>Whole-year bounds of the materialised window, and the row cap that bounds
    /// on-demand extension. All three are overridable for a one-off wider run.</summary>
    internal const int DateWindowMinYearDefault = 1900;
    internal const int DateWindowMaxYearDefault = 2099;
    internal const int DateWindowMaxRowsDefault = 500_000;

    private static int? _dvtWindowMinYear;
    private static int? _dvtWindowMaxYear;
    private static int? _dvtWindowMaxRows;

    internal static int DateWindowMinYear
        => _dvtWindowMinYear ??= ReadYearEnv("AL_RUNNER_DATE_WINDOW_MIN_YEAR", DateWindowMinYearDefault);

    internal static int DateWindowMaxYear
        => _dvtWindowMaxYear ??= ReadYearEnv("AL_RUNNER_DATE_WINDOW_MAX_YEAR", DateWindowMaxYearDefault);

    internal static int DateWindowMaxRows
    {
        get
        {
            if (_dvtWindowMaxRows.HasValue) return _dvtWindowMaxRows.Value;
            var raw = Environment.GetEnvironmentVariable("AL_RUNNER_DATE_WINDOW_MAX_ROWS");
            _dvtWindowMaxRows = int.TryParse(raw, out var v) && v > 0 ? v : DateWindowMaxRowsDefault;
            return _dvtWindowMaxRows.Value;
        }
    }

    private static int ReadYearEnv(string name, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out var v) && v >= 1 && v <= 9999 ? v : fallback;
    }

    /// <summary>
    /// Number of rows the window [min..max] holds across all five period types. Used to
    /// refuse an extension before it is attempted, so the refusal names a number instead of
    /// running out of memory.
    /// </summary>
    internal static long EstimateDateRowCount(DateTime windowStart, DateTime windowEnd)
    {
        if (windowEnd < windowStart) return 0;
        long days = (long)(windowEnd - windowStart).TotalDays + 1;
        long years = windowEnd.Year - windowStart.Year + 1;
        // days (Date) + days/7 (Week) + 12·years (Month) + 4·years (Quarter) + years (Year).
        return days + days / 7 + 17 * years;
    }

    // ── Per-provider populated span ──────────────────────────────────────────────────────
    private sealed class DatePopulatedSpan
    {
        internal DateTime Min = DateTime.MaxValue;
        internal DateTime Max = DateTime.MinValue;
        internal bool Any;
    }

    private static readonly ConditionalWeakTable<object, DatePopulatedSpan> _dvtSpanByProvider = new();

    // Remembered so the find-time guard can extend the window without a fresh data-access handout.
    private static object? _dvtLastDataAccess;
    private static NCLMetaTable? _dvtLastMetaTable;
    private static object? _dvtLastSession;

    private static bool _dvtReflectionReady;
    private static SystemPopulatedValues? _dvtSystemValues;
    private static ConstructorInfo? _dvtCtorReadOnlyBuffer;
    private static ConstructorInfo? _dvtCtorMutableBuffer;
    private static MethodInfo? _dvtTtdpInsert;
    private static object? _dvtInsertOptionsNone;
    private static MethodInfo? _dvtNavIntegerCreate;
    private static MethodInfo? _dvtNavOptionCreate;
    private static MethodInfo? _dvtNavTextCreateTruncated;
    private static MethodInfo? _dvtNavDateCreateDate;
    private static MethodInfo? _dvtGetDefaultNavValue;

    // BC's own period arithmetic — bound once, never re-derived here.
    private static Type? _dvtPeriodTypeEnum;
    private static object? _dvtSortAscending;
    private static MethodInfo? _dvtRoundUp;              // DateTimeHelper.DatePeriodRoundUp(DateTime, DatePeriodType, out DateTime)
    private static MethodInfo? _dvtIsAtStartOfPeriod;    // DateTimeHelper.IsDateAtStartOfPeriod(DateTime, DatePeriodType)
    private static MethodInfo? _dvtPeriodStartMin;       // DateTimeHelper.DatePeriodStartMinimumDate(DatePeriodType)
    private static MethodInfo? _dvtPeriodStartMax;       // DateTimeHelper.DatePeriodStartMaximumDate(DatePeriodType)
    private static MethodInfo? _dvtToNextPeriodStart;    // DateDataProvider.ToNextPeriodStart(DateTime, DatePeriodType, SortOrder)
    private static MethodInfo? _dvtCalcPeriodNumber;     // DateDataProvider.CalculatePeriodNumber(DatePeriodType, DateTime)
    private static MethodInfo? _dvtGetPeriodName;        // DateDataProvider.GetPeriodName(DatePeriodType, DateTime, NavSession, bool)

    // AL option ordinal → BC DatePeriodType, matched BY NAME off the metatable's own option
    // string. Never a positional assumption.
    private static (int Ordinal, object PeriodType)[]? _dvtPeriodTypes;

    /// <summary>True if <paramref name="table"/> is the Date system virtual table (2000000007).</summary>
    private static bool IsDateVirtualTable(NCLMetaTable? table)
        => table != null && table.TableId == DateVirtualTableId;

    /// <summary>
    /// Populate the in-memory store behind the Date (2000000007) data access with one row per
    /// period over the configured window. Idempotent per provider; a later call only fills in
    /// years the provider does not already hold.
    /// </summary>
    private static void PopulateDateVirtualTable(object dataAccess, NCLMetaTable dateMetaTable, object session)
    {
        var windowStart = new DateTime(DateWindowMinYear, 1, 1);
        var windowEnd = new DateTime(DateWindowMaxYear, 12, 31);
        PopulateDateSpan(dataAccess, dateMetaTable, session, windowStart, windowEnd);
    }

    /// <summary>
    /// Materialise every period whose start falls in [<paramref name="wantStart"/>..
    /// <paramref name="wantEnd"/>] that this provider does not already hold, and widen the
    /// provider's recorded span to match. Refuses — loudly — to grow past
    /// AL_RUNNER_DATE_WINDOW_MAX_ROWS.
    /// </summary>
    private static void PopulateDateSpan(
        object dataAccess, NCLMetaTable dateMetaTable, object session, DateTime wantStart, DateTime wantEnd)
    {
        EnsureDateReflection(dateMetaTable);
        EnsureDataAccessProviderReflection(dataAccess);

        var provider = _pDataAccessDataProvider!.GetValue(dataAccess)
            ?? throw new RunnerOutOfScopeException(
                "Date (virtual table 2000000007)",
                "date-virtual-table — Date data access has no in-memory provider; see docs/scope.md");

        // Remember the handout so the find-time guard can extend the window later without one.
        _dvtLastDataAccess = dataAccess;
        _dvtLastMetaTable = dateMetaTable;
        _dvtLastSession = session;

        var span = _dvtSpanByProvider.GetValue(provider, static _ => new DatePopulatedSpan());

        lock (span)
        {
            // Nothing new to do.
            if (span.Any && wantStart >= span.Min && wantEnd <= span.Max) return;

            var newMin = span.Any ? (wantStart < span.Min ? wantStart : span.Min) : wantStart;
            var newMax = span.Any ? (wantEnd > span.Max ? wantEnd : span.Max) : wantEnd;

            var estimate = EstimateDateRowCount(newMin, newMax);
            if (estimate > DateWindowMaxRows)
                throw new RunnerOutOfScopeException(
                    "Date (virtual table 2000000007)",
                    $"date-virtual-table — a Date filter asks for periods in "
                    + $"[{newMin:yyyy-MM-dd}..{newMax:yyyy-MM-dd}], which is about {estimate:N0} rows, past the "
                    + $"{DateWindowMaxRows:N0}-row cap for the materialised window "
                    + $"(currently [{(span.Any ? span.Min : newMin):yyyy-MM-dd}..{(span.Any ? span.Max : newMax):yyyy-MM-dd}]). "
                    + "Raise AL_RUNNER_DATE_WINDOW_MAX_ROWS, or narrow the filter; "
                    + "see docs/limitations.md#date-virtual-table");

            // Same rationale as the Field and Integer tables: make our metatable report
            // IsVirtualTable=false so BC's find takes the NORMAL temp-table DataAccess path
            // over our populated store.
            ClearVirtualBit(dateMetaTable);

            EnsureDateFieldLayout(dateMetaTable);
            var periodTypes = EnsureDatePeriodTypes(dateMetaTable);

            // Insert only the parts of [newMin..newMax] not already covered, so an extension
            // does not re-walk (and re-throw duplicate keys over) the whole existing window.
            foreach (var (start, end) in MissingSpans(span, newMin, newMax))
                foreach (var (ordinal, periodType) in periodTypes)
                    InsertDatePeriods(provider, dateMetaTable, session, ordinal, periodType, start, end);

            span.Min = newMin;
            span.Max = newMax;
            span.Any = true;
        }
    }

    /// <summary>The sub-spans of [newMin..newMax] the provider does not hold yet.</summary>
    private static IEnumerable<(DateTime Start, DateTime End)> MissingSpans(
        DatePopulatedSpan span, DateTime newMin, DateTime newMax)
    {
        if (!span.Any)
        {
            yield return (newMin, newMax);
            yield break;
        }
        if (newMin < span.Min) yield return (newMin, span.Min.AddDays(-1));
        if (newMax > span.Max) yield return (span.Max.AddDays(1), newMax);
    }

    /// <summary>
    /// Insert one row per period of <paramref name="periodType"/> whose start falls in
    /// [<paramref name="from"/>..<paramref name="to"/>]. The first period start is found with
    /// BC's own recipe from DateDataProvider.CountPeriodsWithinRange (Ncl line 127777): a date
    /// already at a period start is used as is, otherwise round up to the end of the period it
    /// falls in and take the next day.
    /// </summary>
    private static void InsertDatePeriods(
        object provider, NCLMetaTable dateMetaTable, object session,
        int periodTypeOrdinal, object periodType, DateTime from, DateTime to)
    {
        var bcMin = (DateTime)_dvtPeriodStartMin!.Invoke(null, new[] { periodType })!;
        var bcMax = (DateTime)_dvtPeriodStartMax!.Invoke(null, new[] { periodType })!;

        var start = from < bcMin ? bcMin : from;
        var end = to > bcMax ? bcMax : to;
        if (start > end) return;

        if (!IsAtStartOfPeriod(start, periodType))
        {
            if (!TryRoundUp(start, periodType, out var roundedUp)) return;
            start = ToNextPeriodStart(roundedUp, DatePeriodTypeDate(), ascending: true);
            if (start > end) return;
        }

        for (var current = start; current <= end;)
        {
            InsertDateRow(provider, dateMetaTable, session, periodTypeOrdinal, periodType, current);
            DateTime next;
            try { next = ToNextPeriodStart(current, periodType, ascending: true); }
            catch (ArgumentOutOfRangeException) { break; }   // ran off 9999-12-31, exactly as BC does
            if (next <= current) break;                       // defensive: never spin
            current = next;
        }
    }

    /// <summary>
    /// Build one Date row and Insert it into the in-memory provider. Layout mirrors what BC's
    /// own DateDataProvider produces (Ncl line 127684-127702): BC's
    /// GetSystemPopulatedVirtualRecordValues fills the system slots, the five Date columns are
    /// written at their own FieldIndex, and everything else gets BC's own default for the
    /// field's type.
    /// </summary>
    private static void InsertDateRow(
        object provider, NCLMetaTable dateMetaTable, object session,
        int periodTypeOrdinal, object periodType, DateTime periodStart)
    {
        // The SystemId key is (tableId, periodType, dayNumber) — unique per row, as BC's own
        // MetadataSystemId is for every other virtual table.
        var dayNumber = (int)(periodStart.Ticks / TimeSpan.TicksPerDay);
        var values = _dvtSystemValues!.Invoke(dateMetaTable, DateVirtualTableId, periodTypeOrdinal, dayNumber, 0);

        if (!TryRoundUp(periodStart, periodType, out var periodEnd))
            throw new RunnerOutOfScopeException(
                "Date (virtual table 2000000007)",
                $"date-virtual-table — BC's DatePeriodRoundUp refused {periodStart:yyyy-MM-dd} for period type "
                + $"{periodType}; see docs/scope.md");

        var periodNo = (int)_dvtCalcPeriodNumber!.Invoke(null, new object?[] { periodType, periodStart })!;

        foreach (var field in GetAllFields(dateMetaTable) ?? Enumerable.Empty<NCLMetaField>())
        {
            var idx = field.FieldIndex;
            if (idx < 0 || idx >= values.Length) continue;
            // Leave the slots BC's own helper already filled (timestamp, SystemId, audit).
            if (values.GetValue(idx) != null) continue;

            object? v = field.FieldNo switch
            {
                DateFieldPeriodType =>
                    _dvtNavOptionCreate!.Invoke(null, new object?[] { field.FieldOptionMetadata, periodTypeOrdinal }),
                DateFieldPeriodStart =>
                    _dvtNavDateCreateDate!.Invoke(null, new object?[] { periodStart, false }),
                // "Period End" is a CLOSING date on the real table — BC builds it as
                // NavDate.CreateDate(roundedUp, closing: true) at Ncl line 127685, and Base
                // Application code calls NormalDate() on it to get the calendar day.
                DateFieldPeriodEnd =>
                    _dvtNavDateCreateDate!.Invoke(null, new object?[] { periodEnd, true }),
                DateFieldPeriodNo =>
                    _dvtNavIntegerCreate!.Invoke(null, new object?[] { periodNo }),
                DateFieldPeriodName =>
                    _dvtNavTextCreateTruncated!.Invoke(null, new object?[] {
                        field.FieldDefinedLength, PeriodName(periodType, periodStart, session, invariant: false) }),
                DateFieldPeriodNameInvariant =>
                    _dvtNavTextCreateTruncated!.Invoke(null, new object?[] {
                        field.FieldDefinedLength, PeriodName(periodType, periodStart, session, invariant: true) }),
                _ => _dvtGetDefaultNavValue!.Invoke(null, new object?[] { field, false }),
            };
            values.SetValue(v, idx);
        }

        var readOnly = _dvtCtorReadOnlyBuffer!.Invoke(new object?[] { dateMetaTable, values });
        var mutable = _dvtCtorMutableBuffer!.Invoke(new object?[] { readOnly });
        try
        {
            _dvtTtdpInsert!.Invoke(provider, new object?[] { 0, mutable, _dvtInsertOptionsNone, null });
        }
        catch (TargetInvocationException tie) when (
            tie.InnerException?.GetType().Name == "NavRecordAlreadyExistsException")
        {
            // ("Period Type", "Period Start") is unique; a repeat means this span was already
            // populated. Faithful to a virtual table where the pair identifies one period.
        }
    }

    // ── Thin wrappers over BC's own arithmetic ───────────────────────────────────────────

    private static bool TryRoundUp(DateTime date, object periodType, out DateTime result)
    {
        var args = new object?[] { date, periodType, null };
        var ok = (bool)_dvtRoundUp!.Invoke(null, args)!;
        result = (DateTime)args[2]!;
        return ok;
    }

    private static bool IsAtStartOfPeriod(DateTime date, object periodType)
        => (bool)_dvtIsAtStartOfPeriod!.Invoke(null, new object?[] { date, periodType })!;

    private static DateTime ToNextPeriodStart(DateTime from, object periodType, bool ascending)
    {
        try
        {
            return (DateTime)_dvtToNextPeriodStart!.Invoke(
                null, new object?[] { from, periodType, _dvtSortAscending })!;
        }
        catch (TargetInvocationException tie) when (tie.InnerException is ArgumentOutOfRangeException inner)
        {
            throw inner;   // the caller treats this exactly as BC's EnumeratePeriods does
        }
    }

    /// <summary>BC's DatePeriodType.Date — the enum value the "next day" step uses.</summary>
    private static object DatePeriodTypeDate() => Enum.ToObject(_dvtPeriodTypeEnum!, 0);

    private static string PeriodName(object periodType, DateTime periodStart, object session, bool invariant)
    {
        try
        {
            return (string)_dvtGetPeriodName!.Invoke(
                null, new object?[] { periodType, periodStart, session, invariant })! ?? string.Empty;
        }
        catch (TargetInvocationException tie)
        {
            throw new RunnerOutOfScopeException(
                "Date (virtual table 2000000007) — \"Period Name\"",
                "date-virtual-table — BC's DateDataProvider.GetPeriodName could not name the period "
                + $"({periodType} starting {periodStart:yyyy-MM-dd}): {tie.InnerException?.Message}. "
                + "The session's FormatSettings are what BC reads for the weekday and month names; "
                + "answering with an invented name would put a wrong caption in a green test. "
                + "See docs/scope.md");
        }
    }

    // ── Metadata resolution ──────────────────────────────────────────────────────────────

    private static bool _dvtLayoutChecked;

    /// <summary>
    /// Confirm the metatable's field numbers still carry the columns BC's own DateDataProvider
    /// assumes they do. BC hardcodes these numbers; we mirror them, but a silent mismatch would
    /// write "Period No." into "Period End", so a shape change says so instead.
    /// </summary>
    private static void EnsureDateFieldLayout(NCLMetaTable dateMetaTable)
    {
        if (_dvtLayoutChecked) return;

        var allFields = (GetAllFields(dateMetaTable) ?? Enumerable.Empty<NCLMetaField>()).ToList();
        var byNo = allFields.Where(f => f != null).ToDictionary(f => f.FieldNo, f => f.FieldName ?? string.Empty);

        void Expect(int fieldNo, string name)
        {
            if (byNo.TryGetValue(fieldNo, out var actual)
                && string.Equals(actual.Replace(" ", string.Empty), name.Replace(" ", string.Empty),
                    StringComparison.OrdinalIgnoreCase))
                return;
            throw new RunnerOutOfScopeException(
                "Date (virtual table 2000000007)",
                $"date-virtual-table — field {fieldNo} of the Date metatable is "
                + $"'{(byNo.TryGetValue(fieldNo, out var a) ? a : "<absent>")}', not '{name}' "
                + $"[fields={string.Join("/", allFields.Select(f => $"{f.FieldNo}:{f.FieldName}"))}] "
                + "— BC metadata shape changed; see docs/scope.md");
        }

        Expect(DateFieldPeriodType, "Period Type");
        Expect(DateFieldPeriodStart, "Period Start");
        Expect(DateFieldPeriodEnd, "Period End");
        Expect(DateFieldPeriodNo, "Period No.");
        Expect(DateFieldPeriodName, "Period Name");
        // Field 6 (the invariant period name) is not present on every BC build, so it is not
        // required — a build without it simply gets BC's default for whatever field 6 is.

        _dvtLayoutChecked = true;
    }

    /// <summary>
    /// Pair each AL "Period Type" option ordinal with BC's own DatePeriodType value of the same
    /// NAME, read off the metatable's own option string. Never a positional assumption: if BC
    /// ever reorders either list, the pairing follows the names.
    /// </summary>
    private static (int Ordinal, object PeriodType)[] EnsureDatePeriodTypes(NCLMetaTable dateMetaTable)
    {
        if (_dvtPeriodTypes != null) return _dvtPeriodTypes;

        var typeField = (GetAllFields(dateMetaTable) ?? Enumerable.Empty<NCLMetaField>())
            .FirstOrDefault(f => f.FieldNo == DateFieldPeriodType)
            ?? throw new RunnerOutOfScopeException(
                "Date (virtual table 2000000007)",
                "date-virtual-table — the Date metatable has no field 1 (\"Period Type\"); see docs/scope.md");

        var optionString = typeField.FieldOptionMetadata?.OptionString
            ?? throw new RunnerOutOfScopeException(
                "Date (virtual table 2000000007)",
                "date-virtual-table — the Date \"Period Type\" field carries no option metadata, so its "
                + "ordinals cannot be resolved; see docs/scope.md");

        var parts = optionString.Split(',');
        var pairs = new List<(int, object)>();
        for (int i = 0; i < parts.Length; i++)
        {
            var name = parts[i].Trim();
            if (name.Length == 0) continue;
            // Only the five BC knows about; an option BC's DatePeriodType has no name for is
            // one this BC build's provider would not enumerate either.
            if (!Enum.TryParse(_dvtPeriodTypeEnum!, name, ignoreCase: true, out var value) || value == null)
                continue;
            pairs.Add((i, value));
        }

        if (pairs.Count == 0)
            throw new RunnerOutOfScopeException(
                "Date (virtual table 2000000007)",
                $"date-virtual-table — none of the Date \"Period Type\" options ('{optionString}') matches a "
                + $"BC DatePeriodType value ('{string.Join(",", Enum.GetNames(_dvtPeriodTypeEnum!))}'); "
                + "see docs/scope.md");

        _dvtPeriodTypes = pairs.ToArray();
        return _dvtPeriodTypes;
    }

    /// <summary>
    /// Bind the row-building and period-arithmetic reflection lazily off the metatable
    /// instance's own assembly, with a hard throw when a member is genuinely absent.
    /// </summary>
    private static void EnsureDateReflection(NCLMetaTable dateMetaTable)
    {
        if (_dvtReflectionReady) return;

        var nclAsm = dateMetaTable.GetType().Assembly;
        const string rt = "Microsoft.Dynamics.Nav.Runtime.";

        Type Need(string name) => nclAsm.GetType(name)
            ?? throw new InvalidOperationException($"{name} not found in Ncl — BC metadata shape changed");

        var tReadOnlyBuffer = Need(rt + "ReadOnlyRecordBuffer");
        var tMutableBuffer = Need(rt + "MutableRecordBuffer");
        var tTempTableProvider = Need(rt + "TempTableDataProvider");
        var tInsertOptions = Need(rt + "InsertOptions");
        var tOptionMetadata = Need(rt + "NCLOptionMetadata");
        var tNavValueMetadata = Need(rt + "INavValueMetadata");

        var tNavValue = ResolveType(rt + "NavValue", "Microsoft.Dynamics.Nav.Types.NavValue")
            ?? throw new InvalidOperationException("NavValue type not found — BC metadata shape changed");
        var tNavInteger = ResolveType(rt + "NavInteger", "Microsoft.Dynamics.Nav.Types.NavInteger")
            ?? throw new InvalidOperationException("NavInteger type not found — BC metadata shape changed");
        var tNavOption = ResolveType(rt + "NavOption", "Microsoft.Dynamics.Nav.Types.NavOption")
            ?? throw new InvalidOperationException("NavOption type not found — BC metadata shape changed");
        var tNavText = ResolveType(rt + "NavText", "Microsoft.Dynamics.Nav.Types.NavText")
            ?? throw new InvalidOperationException("NavText type not found — BC metadata shape changed");
        var tNavDate = ResolveType(rt + "NavDate", "Microsoft.Dynamics.Nav.Types.NavDate")
            ?? throw new InvalidOperationException("NavDate type not found — BC metadata shape changed");

        _dvtSystemValues = SystemPopulatedValues.Bind(nclAsm);

        _dvtCtorReadOnlyBuffer = tReadOnlyBuffer.GetConstructors(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(c => c.GetParameters().Length == 2)
            ?? throw new InvalidOperationException("ReadOnlyRecordBuffer(.,.) not found — BC metadata shape changed");

        _dvtCtorMutableBuffer = tMutableBuffer.GetConstructors(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(c => c.GetParameters().Length == 1
                && tReadOnlyBuffer.IsAssignableFrom(c.GetParameters()[0].ParameterType))
            ?? throw new InvalidOperationException("MutableRecordBuffer(ReadOnlyRecordBuffer) not found — BC metadata shape changed");

        _dvtTtdpInsert = tTempTableProvider.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "Insert" && m.GetParameters().Length == 4)
            ?? throw new InvalidOperationException("TempTableDataProvider.Insert(4 args) not found — BC metadata shape changed");

        _dvtInsertOptionsNone = Enum.ToObject(tInsertOptions, 0);

        // Overload-resolved by hand: the binder reports an ambiguous match for NavInteger.Create(int).
        _dvtNavIntegerCreate = tNavInteger.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "Create" && m.GetParameters().Length == 1
                && m.GetParameters()[0].ParameterType == typeof(int))
            ?? throw new InvalidOperationException("NavInteger.Create(int) not found — BC metadata shape changed");

        _dvtNavOptionCreate = tNavOption.GetMethod("Create", BindingFlags.Public | BindingFlags.Static,
            binder: null, types: new[] { tOptionMetadata, typeof(int) }, modifiers: null)
            ?? throw new InvalidOperationException("NavOption.Create(NCLOptionMetadata,int) not found — BC metadata shape changed");

        _dvtNavTextCreateTruncated = tNavText.GetMethod("CreateTruncated", BindingFlags.Public | BindingFlags.Static,
            binder: null, types: new[] { typeof(int), typeof(string) }, modifiers: null)
            ?? throw new InvalidOperationException("NavText.CreateTruncated(int,string) not found — BC metadata shape changed");

        _dvtNavDateCreateDate = tNavDate.GetMethod("CreateDate", BindingFlags.Public | BindingFlags.Static,
            binder: null, types: new[] { typeof(DateTime), typeof(bool) }, modifiers: null)
            ?? throw new InvalidOperationException("NavDate.CreateDate(DateTime,bool) not found — BC metadata shape changed");

        _dvtGetDefaultNavValue = tNavValue.GetMethod("GetDefaultNavValue",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
            binder: null, types: new[] { tNavValueMetadata, typeof(bool) }, modifiers: null)
            ?? throw new InvalidOperationException(
                "NavValue.GetDefaultNavValue(INavValueMetadata,bool) not found — BC metadata shape changed");

        // ── BC's own period arithmetic ───────────────────────────────────────────────────
        _dvtPeriodTypeEnum = ResolveType(rt + "DatePeriodType", "Microsoft.Dynamics.Nav.Types.DatePeriodType")
            ?? throw new InvalidOperationException("DatePeriodType not found — BC metadata shape changed");
        var tSortOrder = ResolveType(rt + "SortOrder", "Microsoft.Dynamics.Nav.Types.SortOrder")
            ?? throw new InvalidOperationException("SortOrder not found — BC metadata shape changed");
        _dvtSortAscending = Enum.Parse(tSortOrder, "Ascending");

        var tDateTimeHelper = ResolveType(rt + "DateTimeHelper", "Microsoft.Dynamics.Nav.Types.DateTimeHelper")
            ?? throw new InvalidOperationException("DateTimeHelper not found — BC metadata shape changed");
        const BindingFlags anyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        MethodInfo NeedMethod(Type t, string name, int argc) =>
            t.GetMethods(anyStatic).FirstOrDefault(m => m.Name == name && m.GetParameters().Length == argc)
            ?? throw new InvalidOperationException(
                $"{t.Name}.{name}({argc} args) not found — BC metadata shape changed");

        _dvtRoundUp = NeedMethod(tDateTimeHelper, "DatePeriodRoundUp", 3);
        _dvtIsAtStartOfPeriod = NeedMethod(tDateTimeHelper, "IsDateAtStartOfPeriod", 2);
        _dvtPeriodStartMin = NeedMethod(tDateTimeHelper, "DatePeriodStartMinimumDate", 1);
        _dvtPeriodStartMax = NeedMethod(tDateTimeHelper, "DatePeriodStartMaximumDate", 1);

        var tDateProvider = Need(rt + "DateDataProvider");
        _dvtToNextPeriodStart = NeedMethod(tDateProvider, "ToNextPeriodStart", 3);
        _dvtCalcPeriodNumber = NeedMethod(tDateProvider, "CalculatePeriodNumber", 2);
        _dvtGetPeriodName = NeedMethod(tDateProvider, "GetPeriodName", 4);

        _dvtReflectionReady = true;
    }
}
