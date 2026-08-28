// AlValueCapture — the runtime side of --capture-values (issue #1640, second slice of
// the #1640 umbrella; --coverage was the first, #1922). The NavName reflection lookup
// and the wire-value conversion below are shared with --dap's live variable inspection
// (issue #1642) via AlNavNameReflection / AlValueWireFormat — see those files.
//
// #2074 REDESIGN: one record per statement EXECUTION, in order, not one end-of-test
// snapshot. A local reassigned inside a loop that runs N times must produce N records —
// its whole series, not just the final value — because ALchemist's inline loop
// rendering needs the series to show e.g. `myInt = 2 .. 56 (x10)` (SShadowS/ALchemist#1).
// The WIRE SHAPE is unchanged ({scopeName, variableName, value, statementId,
// captureError}, still under `capturedValues`) — only how many entries a variable gets,
// and what each one's statementId means, changes: "just repeated rather than collapsed"
// per the issue text. See ServerProtocol.cs's class doc comment for the wire contract.
//
// MECHANISM — StmtHit for every intermediate execution, Exit() for the last one:
//
// BC's generated code calls StmtHit(N) BEFORE statement N's own side effect runs
// (decompile evidence: `StmtHit(3); this.msg = new NavText("after");` — see this file's
// pre-#2074 header for the full investigation). So the values visible AT StmtHit(N)
// are the result of everything up through statement N-1, not statement N. That is
// exactly the "producing statement" a value should be attributed to: read at
// StmtHit(N), attribute to N-1 (the LAST statement id observed before this call, tracked
// in `_lastStatementId`, not literally N-1 as an integer — control flow means the
// previous StmtHit's own argument is the only true "what ran last" answer). There is no
// StmtHit call after the true final statement, so — same reason the original #1640
// design needed Exit() at all — Exit() takes one more, final snapshot attributed to
// NavMethodScope.StatementNumber (read BEFORE Exit()'s own `statementNumber =
// int.MaxValue` sentinel write).
//
// DIFF, NOT A FULL WALK, ON EVERY OBSERVATION — the actual "one record per execution":
//
// Capturing every [NavName] field on every StmtHit and emitting all of them
// unconditionally would produce (fields x statements) records for a test that touches
// none of them repeatedly — noise, and a different order of runtime cost per statement
// than the old "walk once at Exit" design (the issue's own "measure before shipping"
// warning). Emitting a record only when a field's value (or capture error) actually
// CHANGED since the last observation gives exactly "one record per execution that
// produced a new value" — a loop reassigning the SAME field N times still yields N
// records (each iteration's diff against the previous iteration's value), because each
// iteration's StmtHit is a SEPARATE observation even when consecutive values coincide...
// with one accepted, documented gap: two back-to-back iterations that happen to write
// the IDENTICAL value are indistinguishable from one iteration (see DiffAndUpdate's own
// doc comment) — the issue itself flags this tradeoff ("cannot answer what x was at
// iteration 7 if unchanged") and accepts it as "probably enough for the inline series".
//
// THE FIRST OBSERVATION IS A BASELINE, NEVER EMITTED:
//
// The very first StmtHit call in a scope fires before ANY statement has run, so every
// field is still at its declared-default value — nothing produced that state, so no
// statement earns credit for it (see OnStmtHit's `isBaseline` handling). This is also
// why an AL local that is declared but NEVER assigned anywhere in the scope now gets NO
// record at all — a real, deliberate behaviour change from the pre-#2074 snapshot, which
// walked every [NavName] field unconditionally at Exit() regardless of whether it was
// ever touched. Under "one record per execution", an untouched local was never executed
// into existing, so it has no execution to report. Existing callers that read only the
// LAST entry per variable name for straight-line (single-assignment-per-variable) code
// see the identical values as before; see ServerTests for the updated assertions and
// AlStatementTableTests for the corollary statementId-precision fix (a variable's own
// LAST entry is now attributed to the statement that actually produced it, not
// uniformly to the scope's last statement the way the pre-#2074 design did).
using System.Reflection;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Infrastructure;

/// <summary>One AL local's value, captured at the moment the statement that produced it
/// finished running (issue #2074 — see this file's header for the StmtHit/Exit
/// attribution). <c>StatementId</c> indexes the SAME [SourceSpans] array
/// AlCoverageTracker/AlCallStackCapture already decode, so a caller that also wants the
/// AL source line can resolve it via AlSourceSpanCodec.
///
/// <c>CaptureError</c> (issue #2043) is non-null exactly when this value could not be
/// faithfully read or rendered — either the field read itself threw (reflection failure
/// on the generated `*_Scope` class), or the raw value's own ToString() threw (see
/// AlValueWireFormat). In both cases <c>Value</c> is null, but that null must never be
/// confused with a genuinely null AL variable — a genuinely null variable has
/// <c>CaptureError == null</c>. Naming the exception type in the message is deliberate:
/// either failure mode is unusual and worth being able to see (.claude/rules/
/// loud-failures.md — never drop or fake a value silently).</summary>
public readonly record struct AlCapturedValue(
    string ScopeName, string VariableName, object? Value, int StatementId, string? CaptureError = null);

public static class AlValueCapture
{
    /// <summary>True only while a --capture-values run is executing. Gates both OnStmtHit
    /// and OnExit; the Cecil-rewritten StmtHit/Exit calls are unconditional, this flag is
    /// not — same pattern as AlCoverageTracker.Enabled.</summary>
    public static volatile bool Enabled;

    // Single process-global slots, NOT per-scope: only the OUTERMOST AL call
    // (IsTopLevelCall) is captured (see the file header), and the runner invokes exactly
    // one such call at a time — RunFirstCodeunitOnRun's OnRun invocations run strictly
    // sequentially, matching the same single-slot assumption AlCallStackCapture already
    // makes for the AL call stack.
    private static volatile List<AlCapturedValue>? _series;
    // Last observed (value, captureError) per AL local name, used to detect a genuine
    // change between one observation and the next (see DiffAndUpdate). Reset alongside
    // _series so a new top-level invocation starts with a clean baseline.
    private static volatile Dictionary<string, (object? Value, string? Error)>? _lastKnown;
    // The most recent StmtHit's OWN statement id — i.e. "what just finished running", the
    // producing statement the NEXT observation's diff should be attributed to. -1 means
    // "no StmtHit observed yet this invocation" (the pending-baseline state).
    private static volatile int _lastStatementId = -1;

    /// <summary>Reset before each top-level AL invocation whose locals should be captured.</summary>
    public static void Reset()
    {
        _series = new List<AlCapturedValue>();
        _lastKnown = new Dictionary<string, (object?, string?)>();
        _lastStatementId = -1;
    }

    /// <summary>Every value change observed since the last Reset(), in execution order —
    /// or an empty list if nothing was captured yet (--capture-values on but the
    /// top-level scope has no AL locals ever assigned, or neither StmtHit nor Exit()
    /// fired — e.g. a compile/setup failure before any AL code ran). Never null so
    /// callers don't need a null-check.</summary>
    public static IReadOnlyList<AlCapturedValue> Collect() =>
        _series ?? (IReadOnlyList<AlCapturedValue>)Array.Empty<AlCapturedValue>();

    /// <summary>
    /// Feeds the per-execution series from BC's own StmtHit(N) — called from
    /// AlCoverageTracker.OnStmtHit (the same Cecil-prepended hook site --coverage
    /// already uses; see NclCecilRewrite's StmtHit block), NOT itself a Cecil target.
    /// Self-gated by <see cref="Enabled"/> so the cost of walking every [NavName] field
    /// on every AL statement is paid ONLY on a --capture-values run — a plain corpus run
    /// (captureValues never requested) pays one extra volatile-bool read per statement,
    /// same as AlCoverageTracker.Enabled's own gate.
    ///
    /// The FIRST call for a top-level invocation (<c>_lastStatementId == -1</c>) is a
    /// baseline: it fires before any statement ran, so every field is still at its
    /// declared-default value and nothing produced that state — recorded into
    /// `_lastKnown` so later diffs have something to compare against, but never emitted
    /// (see the file header). Every subsequent call diffs the CURRENT field values
    /// against `_lastKnown` and emits one record per field that actually changed,
    /// attributed to `_lastStatementId` — the statement that just finished running, i.e.
    /// the one whose side effect this observation reflects (see the file header for why
    /// that is N's PREVIOUS statement, not N itself).
    /// </summary>
    public static void OnStmtHit(NavMethodScope scope, int currentStatementNumber)
    {
        if (!Enabled) return;
        if (!scope.IsTopLevelCall) return;
        // NavMethodScope.ExitStatementNumber (int.MaxValue) is written directly by
        // Exit(), never passed to StmtHit by generated code — guarded defensively, same
        // reasoning as AlCoverageTracker.OnStmtHit's own guard.
        if (currentStatementNumber == int.MaxValue) return;

        AlNavNameReflection.EnsureInit();
        var scopeName = scope.ScopeName ?? "?";
        var lastKnown = _lastKnown ??= new Dictionary<string, (object?, string?)>();
        bool isBaseline = _lastStatementId < 0;

        var changed = DiffAndUpdate(scopeName, _lastStatementId, NamedFields(scope), lastKnown, isBaseline);
        if (changed.Count > 0) (_series ??= new List<AlCapturedValue>()).AddRange(changed);
        _lastStatementId = currentStatementNumber;
    }

    /// <summary>
    /// Hook target for the Cecil-rewritten NavMethodScope.Exit() — public static, exactly
    /// (NavMethodScope), prepended before Exit()'s own body. Takes the FINAL diffed
    /// observation, attributed to the real last-executed statement index (read BEFORE
    /// Exit()'s own `statementNumber = int.MaxValue` sentinel write) — this is the only
    /// observation point for whatever the truly last statement changed, since there is
    /// no StmtHit call after it. Never a baseline (`isBaseline: false`): even a scope
    /// whose body never called StmtHit at all (a degenerate empty trigger) still reports
    /// every field once here, because an empty `_lastKnown` makes DiffAndUpdate treat
    /// every field as "changed from nothing observed" — the same backstop the pre-#2074
    /// design got for free by walking every field unconditionally. Must stay
    /// side-effect-free beyond the snapshot: it runs once per AL method invocation,
    /// capture-values or not.
    /// </summary>
    public static void OnExit(NavMethodScope scope)
    {
        if (!Enabled) return;
        // Only the test's own locals — not those of any procedure it calls, which get
        // their own (deeper) scope instances and their own Exit() traffic. IsTopLevelCall
        // (StackDepth == 2, decompiled and confirmed) is true exactly for the scope invoked
        // directly by the runner, i.e. server `execute`'s OnRun today.
        if (!scope.IsTopLevelCall) return;

        AlNavNameReflection.EnsureInit();
        var scopeName = scope.ScopeName ?? "?";
        // Read BEFORE Exit()'s own body runs, so this is the real last-executed statement
        // index, not the int.MaxValue sentinel Exit() is about to write.
        var statementId = scope.StatementNumber;
        var lastKnown = _lastKnown ??= new Dictionary<string, (object?, string?)>();

        var changed = DiffAndUpdate(scopeName, statementId, NamedFields(scope), lastKnown, isBaseline: false);
        if (changed.Count > 0) (_series ??= new List<AlCapturedValue>()).AddRange(changed);
    }

    // Every [NavName]-tagged public instance field on the scope, paired with a delegate
    // that reads its current CLR value — the SAME injectable-delegate shape CaptureField
    // already uses (below), so DiffAndUpdate is testable without a real NavMethodScope.
    private static List<(string Name, Func<object?> ReadField)> NamedFields(NavMethodScope scope)
    {
        var result = new List<(string, Func<object?>)>();
        foreach (var f in scope.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            var name = AlNavNameReflection.GetAlName(f);
            if (name == null) continue;
            result.Add((name, () => f.GetValue(scope)));
        }
        return result;
    }

    /// <summary>
    /// Core diff engine behind the per-execution series (issue #2074): reads each named
    /// field's CURRENT value via <paramref name="fields"/>'s read delegates (through
    /// <see cref="CaptureField"/>, so a read/ToString() failure is reported exactly like
    /// today rather than silently dropped or masking a real change), compares it against
    /// <paramref name="lastKnown"/>, updates <paramref name="lastKnown"/> in place for
    /// every field regardless of outcome, and returns ONLY the fields whose value or
    /// capture error actually changed since the last observation.
    ///
    /// When <paramref name="isBaseline"/> is true, every field is still recorded into
    /// <paramref name="lastKnown"/> but NOTHING is returned — see OnStmtHit's doc comment
    /// for why the very first observation of a scope has no producing statement to credit.
    ///
    /// KNOWN LIMITATION (named in the issue, accepted as a tradeoff, not a bug): two
    /// consecutive executions of the SAME statement that happen to write the IDENTICAL
    /// value are indistinguishable from a single execution — this diff can only tell
    /// "the value is different from last time", not "a statement ran again". A caller
    /// that needs "what was x at iteration 7" for an iteration where x did not change
    /// cannot get that from this series; full per-iteration sampling regardless of value
    /// was considered and rejected as (fields x statements) noise for the common case —
    /// see the file header.
    /// </summary>
    internal static List<AlCapturedValue> DiffAndUpdate(
        string scopeName, int attributionStatementId,
        IEnumerable<(string Name, Func<object?> ReadField)> fields,
        Dictionary<string, (object? Value, string? Error)> lastKnown,
        bool isBaseline)
    {
        var changed = new List<AlCapturedValue>();
        foreach (var (name, readField) in fields)
        {
            var captured = CaptureField(scopeName, name, attributionStatementId, readField);
            if (lastKnown.TryGetValue(name, out var prev)
                && Equals(prev.Value, captured.Value) && prev.Error == captured.CaptureError)
            {
                continue; // no change since the last observation — no execution to report
            }
            lastKnown[name] = (captured.Value, captured.CaptureError);
            if (!isBaseline) changed.Add(captured);
        }
        return changed;
    }

    /// <summary>
    /// Captures one AL local given a way to read its raw CLR value. Extracted so the two
    /// failure modes issue #2043 names — a read that throws, and a ToString() that throws
    /// — are unit-testable without a real NavMethodScope: <paramref name="readField"/> is
    /// exactly <c>() =&gt; f.GetValue(scope)</c> in production, but a test can inject a
    /// throwing delegate directly. Neither failure mode is allowed to propagate out of
    /// this method (this is what OnStmtHit/OnExit call via DiffAndUpdate, and neither may
    /// ever throw — see the file header and AlValueCaptureErrorVisibilityTests).
    /// </summary>
    internal static AlCapturedValue CaptureField(
        string scopeName, string name, int statementId, Func<object?> readField)
    {
        object? raw;
        try { raw = readField(); }
        catch (Exception ex)
        {
            // A field that can't be read is reported, not skipped — a variable silently
            // absent from the list is indistinguishable from one that doesn't exist
            // (.claude/rules/loud-failures.md). Value stays null: nothing was ever read,
            // so nothing is faked.
            return new AlCapturedValue(scopeName, name, null, statementId,
                $"field read threw {ex.GetType().Name}");
        }
        var wireValue = AlValueWireFormat.ToWireValue(raw, out var captureError);
        return new AlCapturedValue(scopeName, name, wireValue, statementId, captureError);
    }
}
