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
//   materialised whole. There is a bounded WINDOW of whole years (default 1900-01-01 ..
//   2099-12-31, 86,885 rows across all five period types) that a read is answered from when the
//   runner cannot see what the read is bounded by.
//
//   NOTHING IS MATERIALISED UNTIL A READ ASKS FOR IT (#2648). The window used to be built at the
//   handout — RecordImplementation.InitializeImpl, i.e. when the `Record Date` VARIABLE is
//   constructed, before any filter exists — so a filter naming one week in 1850 cost ~109,000
//   row inserts (the window, plus a 50-year extension) to return 7 rows, and it made this the
//   most allocation-heavy surface in tests/runner-extras. Now each read materialises what its
//   own request can select:
//
//     * A read whose "Period Start" filter is CLOSED at both ends of every range materialises
//       exactly [lowest low .. highest high]. Safe because BC's own filter engine excludes every
//       row outside those bounds anyway, so a narrower store cannot change an answer.
//     * A keyed Get materialises the one day its key names.
//     * Anything else — no "Period Start" filter, an OPEN bound, a filter shape we cannot read —
//       materialises the whole window, widened by whichever bound IS closed. Those reads ARE
//       answered from the window, so nothing narrower reproduces them.
//
//   FOUR request-carrying guards do the narrowing, not three, because a Record Date read can
//   take four distinct DataAccess paths. They carry four different request types, all deriving
//   from DataCacheRequest, and each needs its own guard:
//
//     AL                                     DataAccess method               request type
//     ---------------------------------------------------------------------------------------
//     Find / FindSet / FindFirst / FindLast  InnerFindAsync                  FindCacheRequest
//     Count                                  CountAsync                      CountCacheRequest
//     IsEmpty                                ExistsAsync                     ExistsCacheRequest
//     Get("Period Type", "Period Start")     InternalTryGetByPrimaryKeyAsync PrimaryKeyCacheRequest
//
//   This header said "a prepend on DataAccess.CountAsync (Count / IsEmpty)" until #3006, and so
//   did the count guard's own comment and docs/limitations.md. IsEmpty() has never reached
//   CountAsync: RecordImplementation.IsEmptyAsync calls its own ExistsAsync, which builds an
//   ExistsCacheRequest (decompiled from Ncl.dll 28.1). Because three comments asserted
//   otherwise, nobody looked, and a closed range outside the window answered IsEmpty() = true
//   with Count() = 7 on the very next line — a wrong answer, since a service tier computes this
//   table across years 1..9999 and answers 7 both ways.
//
//   The find guard lives in RecordPatches.FieldFindIntercept.cs; the other three are prepends
//   registered in NclCecilRewrite.Runtime.cs. All four funnel into the same narrowing helper, so
//   one invariant is maintained in one place rather than four copies drifting apart — which is
//   how the first three came to disagree in the first place.
//
//   Reads that carry NO request — a FlowField whose CalcFormula source is Date, a TableRelation
//   check — reach the in-memory store without passing ANY of the four, so
//   EnsureDateStoreFullyMaterialised is the net under those: it materialises the whole window
//   once per store, from TempTableDataProvider.{Exists,CalcNumeric,CalcMinMax,CalcSums} and from
//   FlowFieldPatches' own source-table read. Measured without it: a `count(Date …)` FlowField
//   answered 0 instead of 73,049. Note that this net is a SEPARATE surface from the ExistsAsync
//   guard above and does not subsume it: the net materialises the DEFAULT window, which does not
//   contain 1850 or 2300, so an AL IsEmpty() over a closed range outside the window still needs
//   its own guard to be answered correctly.
//
//   Past AL_RUNNER_DATE_WINDOW_MAX_ROWS an extension throws RunnerOutOfScopeException naming the
//   requested bound, the window and the cap, rather than answering a larger request with fewer
//   rows.
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
//
// WHAT THE REFUSALS IN THIS FILE CLAIM (#2965)
//   All nine used to end "see docs/scope.md". That file is the manifest of what is
//   PERMANENTLY out of scope -- SMTP, HTTP egress, printing -- and it names no table at all,
//   let alone this one, which this very file implements. The citation was also load-bearing
//   rather than decorative. ApplicationObjectBasePatches.IsPermanentOutOfScope:
//
//       return oos != null && !oos.Reason.StartsWith("not-yet-implemented", StringComparison.Ordinal);
//
//   Under the old anchor that returned TRUE, so an AL [TryFunction] reading the Date table
//   trapped a runner shape gap into `false` -- the silent default .claude/rules/loud-failures.md
//   exists to prevent. They all route through DateShapeGap now, so the refusal tears through.
//
//   Every site was classified before it was touched, in the three buckets
//   RecordPatches.VirtualTableShapeGap.cs defines:
//
//     site                                          what is missing                     bucket
//     ---------------------------------------------------------------------------------------
//     PopulateDateSpan: no in-memory provider       the runner's own store wiring         (2)
//     PopulateDateSpan: past the row cap            the window is bounded; BC is not      (2)
//     InsertDateRow: DatePeriodRoundUp refused      BC's own helper answered no           (2)
//     PeriodName: GetPeriodName threw               BC's own provider threw               (2)
//     EnsureDateFieldLayout: column moved           BC's metatable shape                  (2)
//     EnsureDatePeriodTypes: no field 1             BC's metatable shape                  (2)
//     EnsureDatePeriodTypes: no option metadata     BC's metatable shape                  (2)
//     EnsureDatePeriodTypes: no matching option     BC's metatable shape                  (2)
//     RecordPatches.cs dispatch: no skeleton session  the runner's own state              (2)
//
//   Nothing is in bucket (1), "genuinely out of scope". To be in (1) a refusal has to be
//   faithful to real BC -- BC itself unable to answer, so an AL [TryFunction] reading `false`
//   is the OBSERVABLE BC OUTCOME rather than a runner gap. Real BC computes this table on
//   demand across years 1 through 9999 and never refuses a Date read, so a refusal here is
//   always the runner failing to keep up.
//
//   THE ROW-CAP REFUSAL WAS CHECKED SEPARATELY, because it is not obviously the same category
//   as the other eight: the other eight fire when BC's shape or the runner's store is not what
//   this file needs, while the row cap fires on a perfectly well-formed request that the runner
//   has simply chosen not to serve. It lands in (2) all the same, and for the stronger of the
//   two reasons: a service tier answers that request with rows, so `false` from a [TryFunction]
//   would be a WRONG answer rather than an unavailable one. It is not bucket (3) either --
//   "implementable now" would mean writing the message differently, and the message already
//   says exactly what to do (raise AL_RUNNER_DATE_WINDOW_MAX_ROWS, or narrow the filter). What
//   would actually retire it is computing rows per request instead of materialising a window.
//
//   tests/runner-extras/date-virtual-table-window pins the row-cap refusal with
//   Assert.ExpectedError('out-of-scope: Date (virtual table 2000000007)'), which matches on the
//   API prefix only. RunnerOutOfScopeException.BuildMessage renders
//   "out-of-scope: <api> - <reason> - see <link>", so rewriting the REASON leaves that prefix
//   untouched -- confirmed by running the suite, not assumed.
//
//   One site changed its Api as well as its reason: the "Period Name" refusal used to raise
//   Api = `Date (virtual table 2000000007) - "Period Name"`. OutOfScopeMessage.TryParse cuts
//   the api from the reason at the FIRST em-dash, so that api made the typed and untyped
//   recovery paths disagree -- the same defect #2945 found on the Feature Key Modify surface.
//   The column is named in the DETAIL now, and there is one Date surface rather than two.
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    /// <summary>
    /// Every refusal raised for the Date table, built in one place. See
    /// RecordPatches.VirtualTableShapeGap.cs for the three-bucket classification and for why
    /// the anchor is "not-yet-implemented" rather than a docs/scope.md section (#2945); see
    /// this file's header for the per-site classification behind the nine (#2965).
    /// </summary>
    /// <remarks>
    /// The doc link is this table's OWN limitations section rather than the shared
    /// shape-gaps one, because the window and its row cap are already written up there and
    /// that section is what a reader hitting the cap needs.
    /// </remarks>
    internal static RunnerOutOfScopeException DateShapeGap(string detail)
        => VirtualTableShapeGap(
            "Date (virtual table 2000000007)", "date-virtual-table", detail,
            "docs/limitations.md#date-virtual-table");

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
    /// Upper bound on the number of rows the span [start..end] holds across all five period
    /// types. Used to refuse an extension before it is attempted, so the refusal names a number
    /// instead of running out of memory. It must never UNDER-count: an under-count lets a
    /// request through that then allocates more rows than the cap was meant to allow, which is
    /// why AlRunner.Tests/DateVirtualTableWindowTests.cs checks it against a day-by-day count.
    /// </summary>
    internal static long EstimateDateRowCount(DateTime windowStart, DateTime windowEnd)
    {
        if (windowEnd < windowStart) return 0;
        long days = (long)(windowEnd - windowStart).TotalDays + 1;
        long years = windowEnd.Year - windowStart.Year + 1;
        // days (Date) + at most ceil(days/7) Mondays (Week) + 12·years (Month)
        // + 4·years (Quarter) + years (Year). A span of 366 days holds 53 Mondays, so the
        // week term rounds UP — days/7 undercounts a leap year that starts on a Monday.
        return days + (days + 6) / 7 + 17 * years;
    }

    // ── Per-provider populated span ──────────────────────────────────────────────────────
    private sealed class DatePopulatedSpan
    {
        /// <summary>
        /// The periods this provider holds, as DISJOINT, SORTED, gap-separated intervals rather
        /// than one min..max envelope. The envelope is what made per-request materialisation
        /// (#2648) only half a fix: materialise one week of 1850, then Get a day in 2100, and an
        /// envelope has to fill everything between — 250 years, ~109,000 rows, which is the exact
        /// number the issue is about, just moved to a later call. The runner-extras Date suite
        /// does precisely that in its first two tests. With a set, the second request costs the
        /// ~440 rows of the year it names.
        /// </summary>
        internal readonly List<(DateTime Start, DateTime End)> Covered = new();

        /// <summary>Running row estimate of <see cref="Covered"/>, so the cap is checked against
        /// what is actually materialised rather than against the envelope's span.</summary>
        internal long CoveredEstimate;

        /// <summary>True once the whole configured window has been materialised into this
        /// provider. Distinct from "Covered is non-empty", which is true after ANY span —
        /// including a single day materialised for one keyed Get.</summary>
        internal bool WholeWindow;

        /// <summary>The metatable and skeleton session captured when this provider was handed
        /// out, so a read reaching the provider WITHOUT a DataAccess-level request (a FlowField
        /// calculation, a TableRelation check) can still materialise. Both are per data-access
        /// constants; the handout is the only place they are available together.</summary>
        internal NCLMetaTable? Meta;
        internal object? Session;
    }

    private static readonly ConditionalWeakTable<object, DatePopulatedSpan> _dvtSpanByProvider = new();

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
    /// Called when the Date (2000000007) data access is handed out — which is when a
    /// <c>Record Date</c> VARIABLE is constructed (RecordImplementation.InitializeImpl), before
    /// any filter exists. It materialises NOTHING (#2648). All it does is the one thing the
    /// handout has always done to the METATABLE, so the metatable's state at read time is exactly
    /// what it was when the population lived here.
    /// </summary>
    private static void PrepareDateVirtualTable(object dataAccess, NCLMetaTable dateMetaTable, object session)
    {
        // Same rationale as the Field and Integer tables: make our metatable report
        // IsVirtualTable=false so BC's find takes the NORMAL temp-table DataAccess path over our
        // store. It stays here rather than moving into PopulateDateSpan's caller chain because
        // it must be true of the metatable from the moment the record exists, not from the moment
        // the first row is inserted — which is when the eager population used to do it.
        ClearVirtualBit(dateMetaTable);

        // Register this provider as THE Date store, and park the metatable and session on the
        // registration. Two things depend on it: EnsureDateStoreFullyMaterialised identifies the
        // Date provider by this registration alone (a ConditionalWeakTable miss is every other
        // table, with no reflection and no request), and it needs a metatable and a session to
        // materialise with when it is reached from a read that carries neither.
        EnsureDataAccessProviderReflection(dataAccess);
        if (_pDataAccessDataProvider!.GetValue(dataAccess) is not object provider) return;
        var span = _dvtSpanByProvider.GetValue(provider, static _ => new DatePopulatedSpan());
        lock (span)
        {
            span.Meta ??= dateMetaTable;
            span.Session ??= session;
        }
    }

    /// <summary>
    /// The safety net under per-request materialisation (#2648), called from the reads of the
    /// in-memory store that carry NO DataCacheRequest and so are never seen by the find, count or
    /// keyed-Get guards: TempTableDataProvider.Exists, CalcNumeric, CalcMinMax and CalcSums.
    ///
    /// WHY IT HAS TO EXIST. Measured on this branch with the guards wired one layer up instead:
    /// a `count(Date where …)` FlowField went from 73,049 to 0, an `exist(Date where …)` FlowField
    /// from Yes to No, a `min("Date"."Period Start")` FlowField from 1900-01-01 to blank, and a
    /// TableRelation check against a period outside the window stopped raising. FlowFieldsHelper
    /// and RecordImplementation.ValidateRelation reach the provider without going through
    /// DataAccess.CalcNumericAsync / ExistsAsync at all, so a prepend there applies and never
    /// fires — verified by tracing every request that reached it.
    ///
    /// WHY IT MATERIALISES THE WHOLE WINDOW rather than something narrower. These callers hold
    /// the answer to a question the runner cannot see the bounds of from here, so the only span
    /// that is guaranteed to answer them exactly as the eager scheme did is the one the eager
    /// scheme built. Doing that ONCE per provider, and only when such a read actually happens,
    /// means a bundle that reads Date only through finds, counts and keyed Gets — the shape #2648
    /// is about — never pays for it, and a bundle that does reach one of these paths gets
    /// precisely the pre-#2648 answers.
    ///
    /// For every table but 2000000007 this is one ConditionalWeakTable miss and a return.
    /// </summary>
    public static void EnsureDateStoreFullyMaterialised(object provider)
    {
        if (!_dvtSpanByProvider.TryGetValue(provider, out var span)) return;   // not the Date store
        NCLMetaTable meta;
        object session;
        lock (span)
        {
            if (span.WholeWindow) return;
            if (span.Meta is not NCLMetaTable m || span.Session is not object s) return;
            meta = m;
            session = s;
        }
        PopulateDateSpanIntoProvider(provider, meta, session,
            new DateTime(DateWindowMinYear, 1, 1), new DateTime(DateWindowMaxYear, 12, 31));
    }

    /// <summary>
    /// Materialise the whole configured window (default 1900-01-01 .. 2099-12-31, 86,885 rows).
    /// This is what answers a request that does NOT name a closed bound on both ends of
    /// "Period Start" — an unfiltered read, an open bound, or a filter shape the runner cannot
    /// parse — because such a request is answered FROM the window and no narrower store returns
    /// the same rows. Idempotent per provider.
    /// </summary>
    private static void PopulateDefaultDateWindow(object dataAccess, NCLMetaTable dateMetaTable, object session)
        => PopulateDateSpan(dataAccess, dateMetaTable, session,
            new DateTime(DateWindowMinYear, 1, 1), new DateTime(DateWindowMaxYear, 12, 31));

    /// <summary>
    /// Materialise every period whose start falls in [<paramref name="wantStart"/>..
    /// <paramref name="wantEnd"/>] that this provider does not already hold, and widen the
    /// provider's recorded span to match. Refuses — loudly — to grow past
    /// AL_RUNNER_DATE_WINDOW_MAX_ROWS.
    /// </summary>
    private static void PopulateDateSpan(
        object dataAccess, NCLMetaTable dateMetaTable, object session, DateTime wantStart, DateTime wantEnd)
    {
        EnsureDataAccessProviderReflection(dataAccess);

        var provider = _pDataAccessDataProvider!.GetValue(dataAccess)
            ?? throw DateShapeGap(
                "the Date data access handed over no in-memory provider, so there is nothing to "
                + "populate");

        PopulateDateSpanIntoProvider(provider, dateMetaTable, session, wantStart, wantEnd);
    }

    /// <summary>
    /// The body of <see cref="PopulateDateSpan"/>, reached either through a DataAccess (the find,
    /// count and keyed-Get guards) or with the provider already in hand (the provider-level net,
    /// which has no DataAccess to unwrap).
    /// </summary>
    private static void PopulateDateSpanIntoProvider(
        object provider, NCLMetaTable dateMetaTable, object session, DateTime wantStart, DateTime wantEnd)
    {
        EnsureDateReflection(dateMetaTable);

        var span = _dvtSpanByProvider.GetValue(provider, static _ => new DatePopulatedSpan());

        lock (span)
        {
            var missing = DateMissingSpans(span.Covered, wantStart, wantEnd);
            if (missing.Count == 0) return;   // nothing new to do

            // The cap is about how many rows are materialised, so it is checked against what is
            // already there PLUS what this request would add — not against the envelope, which
            // would refuse a narrow request purely because an earlier one sat far away.
            long adding = 0;
            foreach (var (s0, e0) in missing) adding += EstimateDateRowCount(s0, e0);
            var total = span.CoveredEstimate + adding;
            if (total > DateWindowMaxRows)
                throw DateShapeGap(
                    "a Date filter asks for periods in "
                    // #2968: `:N0` picks up the ambient group separator, so this diagnostic
                    // read differently per operator locale. Invariant, like the rest of the
                    // runner's own output.
                    + System.FormattableString.Invariant(
                        $"[{wantStart:yyyy-MM-dd}..{wantEnd:yyyy-MM-dd}], which would add about {adding:N0} rows ")
                    + System.FormattableString.Invariant(
                        $"for {total:N0} in all, past the {DateWindowMaxRows:N0}-row cap for the materialised ")
                    + "window "
                    + (span.Covered.Count > 0
                        ? System.FormattableString.Invariant(
                              $"(currently {span.CoveredEstimate:N0} rows in {span.Covered.Count} span(s), ")
                          + System.FormattableString.Invariant(
                              $"[{span.Covered[0].Start:yyyy-MM-dd}..{span.Covered[^1].End:yyyy-MM-dd}]). ")
                        : "(nothing is materialised yet — Date rows are materialised per request). ")
                    + "Raise AL_RUNNER_DATE_WINDOW_MAX_ROWS, or narrow the filter");

            // Belt and braces: PrepareDateVirtualTable already cleared this at handout, and it
            // is idempotent. It stays here so a future caller that reaches PopulateDateSpan by
            // some other route cannot leave the bit set.
            ClearVirtualBit(dateMetaTable);

            EnsureDateFieldLayout(dateMetaTable);
            var periodTypes = EnsureDatePeriodTypes(dateMetaTable);

            // Insert only the parts this provider does not already hold, so a second request
            // does not re-walk (and re-throw duplicate keys over) what a first one materialised.
            foreach (var (start, end) in missing)
                foreach (var (ordinal, periodType) in periodTypes)
                    InsertDatePeriods(provider, dateMetaTable, session, ordinal, periodType, start, end);

            DateAddCovered(span.Covered, wantStart, wantEnd);
            span.CoveredEstimate = total;
            span.Meta ??= dateMetaTable;
            span.Session ??= session;
            // "The whole configured window is in" — the flag the provider-level net reads. It is
            // deliberately about the WINDOW, not about anything being materialised: a single day
            // materialised for a keyed Get must not convince the net that a FlowField can be
            // answered. DateMissingSpans is the authority, so a window that was covered by two
            // adjacent requests counts, and one with a hole in it does not.
            if (DateMissingSpans(span.Covered,
                    new DateTime(DateWindowMinYear, 1, 1), new DateTime(DateWindowMaxYear, 12, 31)).Count == 0)
                span.WholeWindow = true;
        }
    }

    /// <summary>
    /// The sub-spans of [<paramref name="wantStart"/>..<paramref name="wantEnd"/>] that
    /// <paramref name="covered"/> does not already hold, in order. <paramref name="covered"/>
    /// must be disjoint and sorted by Start, which <see cref="DateAddCovered"/> maintains.
    /// Returns an empty list when the request is entirely covered, or inverted.
    /// </summary>
    internal static List<(DateTime Start, DateTime End)> DateMissingSpans(
        IReadOnlyList<(DateTime Start, DateTime End)> covered, DateTime wantStart, DateTime wantEnd)
    {
        var gaps = new List<(DateTime Start, DateTime End)>();
        if (wantEnd < wantStart) return gaps;

        var cursor = wantStart;
        foreach (var (start, end) in covered)
        {
            if (end < cursor) continue;             // entirely before what is still wanted
            if (start > wantEnd) break;             // sorted, so nothing later can overlap either
            if (start > cursor) gaps.Add((cursor, start.AddDays(-1)));
            if (end >= cursor) cursor = end.AddDays(1);
            if (cursor > wantEnd) return gaps;
        }
        if (cursor <= wantEnd) gaps.Add((cursor, wantEnd));
        return gaps;
    }

    /// <summary>
    /// Record [<paramref name="start"/>..<paramref name="end"/>] as covered, keeping
    /// <paramref name="covered"/> disjoint, sorted by Start, and merged across intervals that
    /// touch or overlap — so a window materialised as two adjacent halves reads as one span and
    /// not as two with a zero-day gap between them.
    /// </summary>
    internal static void DateAddCovered(
        List<(DateTime Start, DateTime End)> covered, DateTime start, DateTime end)
    {
        if (end < start) return;

        var i = 0;
        while (i < covered.Count && covered[i].End.AddDays(1) < start) i++;   // strictly before, no touch

        var newStart = start;
        var newEnd = end;
        while (i < covered.Count && covered[i].Start.AddDays(-1) <= newEnd)
        {
            if (covered[i].Start < newStart) newStart = covered[i].Start;
            if (covered[i].End > newEnd) newEnd = covered[i].End;
            covered.RemoveAt(i);
        }
        covered.Insert(i, (newStart, newEnd));
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
            throw DateShapeGap(
                $"BC's own DateTimeHelper.DatePeriodRoundUp refused {periodStart:yyyy-MM-dd} for period "
                + $"type {periodType}, so the row's \"Period End\" cannot be built");

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

    // ── Find-time window guard ───────────────────────────────────────────────────────────

    private static PropertyInfo? _dvtDaSession;
    private static MethodInfo? _dvtToRangeList;      // FilterExpression.ToRangeList(ISortingRulesProvider)
    private static PropertyInfo? _dvtRangeListRanges;
    private static PropertyInfo? _dvtRangeLowIsMin, _dvtRangeHighIsMax, _dvtRangeLowValue,
        _dvtRangeHighValue, _dvtRangeIsEmpty;
    private static MethodInfo? _dvtNavValueToDateTime;
    private static bool _dvtGuardReady;

    /// <summary>
    /// Called from the InnerFindAsync guard for EVERY find on table 2000000007, and prepended to
    /// DataAccess.CountAsync for every Count()/IsEmpty(), before BC's own read runs. It
    /// materialises exactly the periods THIS REQUEST can select, which since #2648 is the only
    /// place Date rows are materialised at all:
    ///
    ///   * Every non-empty range of the "Period Start" filter is closed at BOTH ends → the rows
    ///     in [lowest low .. highest high] are the only rows the filter can select, so those are
    ///     the only rows materialised. BC's own filter engine excludes everything outside those
    ///     bounds regardless of what the store holds, so a narrower store cannot change a single
    ///     answer. That is the whole safety argument, and it is why the predicate is "every range
    ///     closed at both ends" rather than "some bound is closed".
    ///
    ///   * Anything else — no "Period Start" filter at all, an OPEN bound (`'%1..'`, `'..%1'`, or
    ///     a bound sitting at BC's own first/last period start), a filter shape ToRangeList
    ///     cannot express, a request we cannot read → the whole configured window, widened to
    ///     cover whichever bound IS closed. An open bound is answered FROM the window: BC would
    ///     answer it out to year 1 or year 9999, and materialising 3.6 million rows is not on the
    ///     table. That single approximation is documented in docs/limitations.md and is
    ///     deliberately unchanged here.
    ///
    /// Past AL_RUNNER_DATE_WINDOW_MAX_ROWS, PopulateDateSpan throws instead of answering a wider
    /// request with fewer rows.
    /// </summary>
    internal static void EnsureDateWindowCoversRequest(object dataAccess, object cacheRequest)
    {
        // A `Record Date temporary` holds exactly the rows AL inserted -- materialising into its
        // private store injected real Date rows AL never wrote (measured: Count went 1 -> 31
        // across one filtered Count(), and the subsequent FindSet returned an injected row
        // instead of AL's). Issue #2524.
        if (IsTemporaryRecordDataAccess(dataAccess)) return;

        NCLMetaTable meta;
        object session;
        try
        {
            if (_pReqMaoLight?.GetValue(cacheRequest) is not NCLMetaTable m) return;
            meta = m;
            EnsureDateReflection(meta);
            EnsureDateGuardReflection(dataAccess, cacheRequest);

            if (_dvtDaSession!.GetValue(dataAccess) is not object s) return;
            session = s;
        }
        catch (RunnerOutOfScopeException) { throw; }
        catch
        {
            // We could not identify the request or the store behind it, so there is nothing to
            // materialise against and no answer to protect. Unchanged from before #2648.
            return;
        }

        DateTime? closedLow, closedHigh;
        bool fullyBounded;
        try
        {
            fullyBounded = TryReadClosedPeriodStartBounds(cacheRequest, session, out closedLow, out closedHigh);
        }
        catch (RunnerOutOfScopeException) { throw; }
        catch
        {
            // Reading the filter is best-effort. A shape we cannot parse falls back to the whole
            // window — the widest thing the request could be answered from — never to a narrower
            // store, which WOULD answer something different now that nothing is materialised up
            // front.
            fullyBounded = false;
            closedLow = closedHigh = null;
        }

        if (fullyBounded && closedLow is DateTime lo && closedHigh is DateTime hi)
        {
            PopulateDateSpan(dataAccess, meta, session, lo, hi);
            return;
        }

        // The whole configured window, WIDENED by whichever bound the filter did close. Both
        // sides are clamped to the window rather than substituted for it, because a range list
        // like `'..%1|%2..'` closes a HIGH bound on its first range and a LOW bound on its
        // second: taking those as the span produces low=2099-12-30, high=1900-01-02, an inverted
        // span that materialises nothing. That inversion was harmless before #2648 only because
        // the window was already in place and PopulateDateSpan's own min/max widened it away;
        // with nothing materialised up front it returned 0 rows where BC returns 4 (measured).
        var lowBound = closedLow is DateTime cl && cl < new DateTime(DateWindowMinYear, 1, 1)
            ? cl : new DateTime(DateWindowMinYear, 1, 1);
        var highBound = closedHigh is DateTime ch && ch > new DateTime(DateWindowMaxYear, 12, 31)
            ? ch : new DateTime(DateWindowMaxYear, 12, 31);

        // PopulateDateSpan only ever widens, and returns immediately when the span already
        // covers what was asked for — the common case after the first such read.
        PopulateDateSpan(dataAccess, meta, session, lowBound, highBound);
    }

    /// <summary>
    /// Reads the request's "Period Start" filter through BC's own FilterExpression.ToRangeList.
    /// <paramref name="low"/> and <paramref name="high"/> come back as the lowest closed low
    /// bound and the highest closed high bound any range names, or null when no range names one —
    /// which is exactly the pair the pre-#2648 guard used to widen the window by.
    /// </summary>
    /// <returns>
    /// True only when the filter names at least one non-empty range AND every non-empty range is
    /// closed at both ends, i.e. when [low..high] provably contains every row the filter can
    /// select. False means the request reaches past any bounded span, so the caller must fall
    /// back to the documented window.
    /// </returns>
    private static bool TryReadClosedPeriodStartBounds(
        object cacheRequest, object session, out DateTime? low, out DateTime? high)
    {
        low = null;
        high = null;

        var filter = FindPeriodStartFilter(cacheRequest);
        if (filter == null) return false;

        var rangeList = _dvtToRangeList!.Invoke(filter, new[] { session });
        if (rangeList == null) return false;
        if (_dvtRangeListRanges!.GetValue(rangeList) is not System.Collections.IEnumerable ranges) return false;

        // BC's own first and last period start for period type Date — the widest the real table
        // ever goes. A bound at or past either end is the filter saying "no limit", and is
        // treated as open, exactly as it was before #2648.
        var bcFirst = (DateTime)_dvtPeriodStartMin!.Invoke(null, new[] { DatePeriodTypeDate() })!;
        var bcLast = (DateTime)_dvtPeriodStartMax!.Invoke(null, new[] { DatePeriodTypeDate() })!;

        var sawRange = false;
        var everyRangeClosed = true;

        foreach (var range in ranges)
        {
            if (range == null) continue;
            if ((bool)_dvtRangeIsEmpty!.GetValue(range)!) continue;
            sawRange = true;

            var lowClosed = false;
            var highClosed = false;

            if (!(bool)_dvtRangeLowIsMin!.GetValue(range)!
                && ToDateTimeOrNull(_dvtRangeLowValue!.GetValue(range)) is DateTime lo
                && lo > bcFirst)
            {
                lowClosed = true;
                low = low == null || lo < low ? lo : low;
            }

            if (!(bool)_dvtRangeHighIsMax!.GetValue(range)!
                && ToDateTimeOrNull(_dvtRangeHighValue!.GetValue(range)) is DateTime hi
                && hi < bcLast)
            {
                highClosed = true;
                high = high == null || hi > high ? hi : high;
            }

            // One half-open range in a `'..%1|%2..'` shape makes the whole filter unbounded: the
            // union it selects reaches past anything [low..high] would hold.
            if (!lowClosed || !highClosed) everyRangeClosed = false;
        }

        return sawRange && everyRangeClosed;
    }

    /// <summary>
    /// Prepended to DataAccess.CountAsync(CountCacheRequest) for every table. Record.Count()
    /// takes the count path, not the find path, so the InnerFindAsync guard never sees it;
    /// without this a Count over a range outside what is materialised would return however many
    /// rows happen to be there. For every table but 2000000007 this does one integer comparison
    /// and returns.
    ///
    /// <para>This comment used to say "Record.Count() AND IsEmpty()", and that was wrong — see
    /// <see cref="DataAccess_DateWindowGuardForExists"/> below. The claim went unchallenged long
    /// enough to hide a wrong answer for a whole release, which is why the correction is spelled
    /// out rather than quietly edited: IsEmpty() has never reached CountAsync.</para>
    ///
    /// <para>Which DataAccess entry points are worth guarding was measured twice, from two
    /// directions, and the two results are easy to mistake for a contradiction:</para>
    /// <list type="bullet">
    /// <item>A FlowField calculation and a TableRelation check never reach DataAccess at all —
    /// they go straight to the in-memory provider — so a prepend on ExistsAsync /
    /// CalcNumericAsync / CalcMinMaxAsync / CalcSumsAsync applies and then never fires for
    /// THOSE callers (#2648). That is why the provider-level net
    /// <see cref="EnsureDateStoreFullyMaterialised"/> exists.</item>
    /// <item>An AL <c>Record.IsEmpty()</c> DOES reach DataAccess.ExistsAsync, and a prepend
    /// there fires (#3006): before one existed, a closed range outside the window answered
    /// IsEmpty() = true and Count() = 7 on the very next line.</item>
    /// </list>
    /// <para>So ExistsAsync is guarded and CalcNumericAsync / CalcMinMaxAsync / CalcSumsAsync
    /// are not. The net does not subsume the ExistsAsync guard: the net materialises the DEFAULT
    /// window, which does not contain 1850, so IsEmpty() over a closed range outside the window
    /// still needs a guard that reads that range off the request.</para>
    /// </summary>
    public static void DataAccess_DateWindowGuardForCount(object self, object request)
    {
        if (FindRequestTableId(request) != DateVirtualTableId) return;
        EnsureDateWindowCoversRequest(self, request);
    }

    /// <summary>
    /// Prepended to DataAccess.ExistsAsync(ExistsCacheRequest) for every table — the FOURTH
    /// request path into this table, and the one the count guard's comment above wrongly
    /// claimed to cover (issue #3006).
    ///
    /// <para><c>Record.IsEmpty()</c> does not take the count path. Decompiled from Ncl.dll
    /// 28.1: <c>NavRecord.GetALIsEmptyAsync</c> -> <c>RecordImplementation.IsEmptyAsync()</c>
    /// -> <c>RecordImplementation.IsEmptyAsync(FiltersAndMarks)</c> -> its own
    /// <c>ExistsAsync(FiltersAndMarks, SecurityFiltering)</c> ->
    /// <c>dataAccess.ExistsAsync(new ExistsCacheRequest(...))</c>. <c>CountAsync</c> is never
    /// on that chain. <c>ExistsCacheRequest</c> derives from <c>DataCacheRequest</c> exactly as
    /// the find, count and primary-key requests do, so it carries the same
    /// <c>MetaApplicationObject</c> and <c>FiltersAndMarks</c> this guard reads — the guard
    /// simply was never registered on it.</para>
    ///
    /// <para>Measured on main before this existed, one process, one record variable, on
    /// consecutive lines:</para>
    /// <code>
    ///   Date.SetRange("Period Start", 18500101D..18500107D);
    ///   IsEmpty()  -> TRUE
    ///   Count()    -> 7
    /// </code>
    /// <para>On a real service tier the Date table spans years 1 through 9999 and both answer
    /// the same thing, so TRUE is a wrong answer rather than a missing feature — and a quiet
    /// one, since "this range holds no periods" is what an IsEmpty() returning true normally
    /// means.</para>
    ///
    /// <para><c>DataAccess.ExistsAsync</c> is a large async state machine, so unlike the tiny
    /// <c>FindAsync</c> it is not R2R-inlined past a prepend. For every table but 2000000007
    /// this does one integer comparison and returns.</para>
    /// </summary>
    public static void DataAccess_DateWindowGuardForExists(object self, object request)
    {
        if (FindRequestTableId(request) != DateVirtualTableId) return;
        EnsureDateWindowCoversRequest(self, request);
    }

    /// <summary>
    /// Prepended to DataAccess.InternalTryGetByPrimaryKeyAsync for every table. A full-primary-key
    /// <c>Record.Get()</c> never reaches InnerFindAsync — DataAccess has its OWN primary-key path
    /// straight to provider.TryGetByPrimaryKeyAsync — so neither the find guard nor the count
    /// guard sees it, and the Date window was never extended for a keyed read.
    ///
    /// Measured on main before this existed, each call in its own process so no earlier read had
    /// already widened the window:
    ///
    ///   Date.Get(Date, 18500101D)                          -> FALSE  (no such period)
    ///   Date.SetRange(...18500101D..18500107D); FindFirst() -> TRUE, 1850-01-01
    ///   Date.Get(Date, 19500101D)                          -> TRUE   (inside the window)
    ///
    /// The same period, reachable one way and not the other. On a real service tier the Date
    /// table spans years 1..9999 and a keyed Get simply answers, so FALSE here is a wrong answer
    /// rather than a missing feature — and a quiet one, because "this period does not exist" is
    /// exactly what a Get returning false normally means.
    ///
    /// This is the same gap #2504 fixed for the Aggregate Permission Set table on this very
    /// method; the Date table shares the primary-key path and was left behind. The window is
    /// widened from the RECORD ID rather than from a filter, because a keyed Get carries its
    /// key there and may carry no "Period Start" filter at all.
    ///
    /// For every table but 2000000007 this is one integer comparison and returns.
    /// </summary>
    public static void DataAccess_DateWindowGuardForGet(object self, object request)
    {
        if (FindRequestTableId(request) != DateVirtualTableId) return;

        // Same reason as the find guard: a `Record Date temporary` holds only what AL inserted,
        // and widening the materialised window into its private store injects rows AL never
        // wrote (#2524).
        if (IsTemporaryRecordDataAccess(self)) return;

        NCLMetaTable meta;
        object session;
        DateTime? wanted;
        bool hasRecordId;
        try
        {
            if (_pReqMaoLight?.GetValue(request) is not NCLMetaTable m) return;
            meta = m;
            EnsureDateReflection(meta);
            EnsureDateGuardReflection(self, request);
            if (_dvtDaSession!.GetValue(self) is not object s) return;
            session = s;
            wanted = PrimaryKeyPeriodStart(request, out hasRecordId);
        }
        catch (RunnerOutOfScopeException) { throw; }
        catch
        {
            // We could not identify the request or the store behind it — nothing to materialise
            // against. Unchanged from before #2648.
            return;
        }

        if (wanted != null)
        {
            // One day is all a keyed Get needs; PopulateDateSpan widens to whole periods itself
            // and returns immediately when the span already covers it, which is the common case.
            PopulateDateSpan(self, meta, session, wanted.Value, wanted.Value);
            return;
        }

        // A SystemId-keyed Get names no period, and cannot name one the store does not already
        // hold: the SystemId of a row that was never materialised has never been handed out.
        if (!hasRecordId) return;

        // A primary-key Get whose key we could not read. Before #2648 the whole window was
        // already materialised by the time any Get ran, so such a Get answered from the window;
        // fall back to exactly that rather than answering from a narrower store.
        PopulateDefaultDateWindow(self, meta, session);
    }

    /// <summary>
    /// The "Period Start" out of a primary-key request's RecordId. Date's primary key is
    /// ("Period Type", "Period Start"), so it is the SECOND key value.
    /// </summary>
    /// <param name="hasRecordId">
    /// False for a SystemIdCacheRequest, which carries no RecordId at all — a Get by SystemId
    /// cannot name a period the store does not already hold, because the SystemId of an
    /// unmaterialised row has never been handed out. True with a null return means "this IS a
    /// primary-key Get but the key could not be read", which the caller has to answer
    /// conservatively; the two cases were indistinguishable while everything was materialised up
    /// front, and are not any more.
    /// </param>
    private static DateTime? PrimaryKeyPeriodStart(object request, out bool hasRecordId)
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
            if (fields is not System.Collections.IList list || list.Count < 2) return null;

            return ToDateTimeOrNull(list[1]);
        }
        catch
        {
            // Unreadable, not absent: leave hasRecordId as whatever we established, so the caller
            // widens rather than assuming a SystemId request.
            return null;
        }
    }

    /// <summary>The "Period Start" (field 2) FilterExpression on this find request, if any.</summary>
    private static object? FindPeriodStartFilter(object cacheRequest)
    {
        var fam = _pFiltersAndMarks!.GetValue(cacheRequest);
        if (fam == null) return null;
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
            if (_pFieldNo?.GetValue(fieldMeta) is int fieldNo && fieldNo == DateFieldPeriodStart)
                return expr;
        }
        return null;
    }

    private static DateTime? ToDateTimeOrNull(object? navValue)
    {
        if (navValue == null) return null;
        try
        {
            var r = _dvtNavValueToDateTime!.Invoke(navValue, null);
            return r is DateTime dt ? dt.Date : null;
        }
        catch { return null; }
    }

    private static void EnsureDateGuardReflection(object dataAccess, object cacheRequest)
    {
        if (_dvtGuardReady) return;

        // The filter-expression accessors are shared with the Field-table find interception;
        // EnsureFilterReflection binds those but not DataCacheRequest.FiltersAndMarks itself
        // (that one belongs to the heavy Field-only bind), so it is resolved here.
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
        _dvtDaSession = tDataAccess.GetProperty("Session", anyInstance)
            ?? tDataAccess.GetProperty("session", anyInstance)
            ?? throw new InvalidOperationException("DataAccess.Session not found — BC metadata shape changed");

        var tFilterExpr = nclAsm.GetType(rt + "FilterExpression")
            ?? throw new InvalidOperationException("FilterExpression not found — BC metadata shape changed");
        _dvtToRangeList = tFilterExpr.GetMethods(anyInstance)
            .FirstOrDefault(m => m.Name == "ToRangeList" && m.GetParameters().Length == 1)
            ?? throw new InvalidOperationException("FilterExpression.ToRangeList(1 arg) not found — BC metadata shape changed");

        var tRangeList = nclAsm.GetType(rt + "RangeList")
            ?? throw new InvalidOperationException("RangeList not found — BC metadata shape changed");
        _dvtRangeListRanges = tRangeList.GetProperty("Ranges", anyInstance)
            ?? throw new InvalidOperationException("RangeList.Ranges not found — BC metadata shape changed");

        var tRange = nclAsm.GetType(rt + "Range")
            ?? throw new InvalidOperationException("Range not found — BC metadata shape changed");
        PropertyInfo NeedProp(string name) => tRange.GetProperty(name, anyInstance)
            ?? throw new InvalidOperationException($"Range.{name} not found — BC metadata shape changed");
        _dvtRangeLowIsMin = NeedProp("IsLowIsMinimum");
        _dvtRangeHighIsMax = NeedProp("IsHighMaximum");
        _dvtRangeLowValue = NeedProp("LowValue");
        _dvtRangeHighValue = NeedProp("HighValue");
        _dvtRangeIsEmpty = NeedProp("IsEmptyRange");

        var tNavValue = ResolveType(rt + "NavValue", "Microsoft.Dynamics.Nav.Types.NavValue")
            ?? throw new InvalidOperationException("NavValue type not found — BC metadata shape changed");
        _dvtNavValueToDateTime = tNavValue.GetMethod("ToDateTime",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null, types: Type.EmptyTypes, modifiers: null)
            ?? throw new InvalidOperationException("NavValue.ToDateTime() not found — BC metadata shape changed");

        _dvtGuardReady = true;
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
            // The caller treats this exactly as BC's EnumeratePeriods does. Rethrown via
            // ExceptionDispatchInfo, not `throw inner` (#2948), so BC's own frames inside
            // ToNextPeriodStart survive instead of the trace starting here.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(inner).Throw();
            throw; // unreachable
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
            throw DateShapeGap(
                "BC's own DateDataProvider.GetPeriodName could not name the period for the "
                + $"\"Period Name\" column ({periodType} starting {periodStart:yyyy-MM-dd}): "
                + $"{tie.InnerException?.Message}. The session's FormatSettings are what BC reads "
                + "for the weekday and month names; answering with an invented name would put a "
                + "wrong caption in a green test");
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
            throw DateShapeGap(
                $"field {fieldNo} of the Date metatable is "
                + $"'{(byNo.TryGetValue(fieldNo, out var a) ? a : "<absent>")}', not '{name}' "
                + $"[fields={string.Join("/", allFields.Select(f => $"{f.FieldNo}:{f.FieldName}"))}] "
                + "- BC's own column layout moved, and writing a value at the old slot would put "
                + "\"Period No.\" into \"Period End\"");
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
            ?? throw DateShapeGap(
                "the Date metatable has no field 1 (\"Period Type\"), which is the field every row "
                + "is keyed on");

        var optionString = typeField.FieldOptionMetadata?.OptionString
            ?? throw DateShapeGap(
                "the Date \"Period Type\" field carries no option metadata, so its ordinals cannot be "
                + "resolved and a guessed one would mis-key every row");

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
            throw DateShapeGap(
                $"none of the Date \"Period Type\" options ('{optionString}') matches a "
                + $"BC DatePeriodType value ('{string.Join(",", Enum.GetNames(_dvtPeriodTypeEnum!))}'), "
                + "so there is no period type to enumerate");

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
