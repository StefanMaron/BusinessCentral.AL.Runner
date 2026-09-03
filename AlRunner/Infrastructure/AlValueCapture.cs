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
// DIFF PLUS WRITE SET ON EVERY OBSERVATION — the actual "one record per execution":
//
// Capturing every [NavName] field on every StmtHit and emitting all of them
// unconditionally would produce (fields x statements) records for a test that touches
// none of them repeatedly — noise, and a different order of runtime cost per statement
// than the old "walk once at Exit" design. Two things earn a record at an observation:
//   1. the field's value (or capture error) CHANGED since the last observation — the
//      diff, which needs no knowledge of the source; and
//   2. the statement that just finished ASSIGNS the field (its write set, from the AL
//      syntax tree — AlWriteSetModel.cs / AlMemberSyntaxIndex.CollectWrites), whether or
//      not the value changed. This is what makes "x := 5 ran again while x was already
//      5" a record: the contract agreed on SShadowS/ALchemist#1 for #2074/#2056 is full
//      fidelity by default, because a consumer answering "what was x at iteration 7"
//      cannot reconstruct it from a change-only series.
// A field neither changed nor assigned still gets nothing — an untouched local is not
// "executed into existing". What syntax cannot see (a `var` parameter of a user
// procedure, a receiver mutated inside an expression) falls back to the diff alone; see
// AlWriteSetModel.cs.
//
// PER SCOPE INSTANCE, NOT PROCESS-GLOBAL:
//
// The last-known map and the last statement id live in a frame per NavMethodScope
// INSTANCE (AlScopeFrames). A single global pair was wrong the moment the top-level
// scope called a procedure: the callee's `s` was diffed against the caller's `s` (both
// are [NavName] fields keyed by the AL name), the callee's first observation was never a
// baseline, and its first records were attributed to the CALLER's last statement id.
// IsTopLevelCall is true for every scope of the run's root object (StackDepth only grows
// across an application-object boundary — see AlDapSession.cs), so all of them are
// captured, each against its own frame.
//
// THE FIRST OBSERVATION IS A BASELINE, NEVER EMITTED (with one measured exception):
//
// The very first StmtHit call in a scope fires before ANY statement has run, so every
// field is still at its declared-default value — nothing produced that state, so no
// statement earns credit for it (see OnStmtHit's `isBaseline` handling). The exception
// (#2056): BC emits a `for`/`foreach` loop's variable initialisation BEFORE the loop
// statement's own StmtHit (measured on the statement table: `i = 1` is observed AT the
// `for` statement's hit, attributed to the statement before it), so when the loop is the
// scope's FIRST statement the baseline already sees the initial value. AL locals always
// start at their type's default, so a CLR primitive/string that is non-default at the
// baseline can only have been assigned by statement 0's pre-hit part — it is emitted,
// attributed to statement 0 (DiffAndUpdate's AssignedBeforeFirstHit). Without this,
// iteration 1 of a leading `for` loop has no loop variable at all. This is also
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

    // The flat series is one ordered list for the whole invocation (every scope's
    // records, in observation order); the diff STATE is per scope instance — see the
    // file header and AlScopeFrames.
    private static volatile List<AlCapturedValue>? _series;

    private sealed class CaptureState
    {
        // Last observed (value, captureError) per AL local name of THIS scope instance,
        // used to detect a genuine change between one observation and the next.
        public readonly Dictionary<string, (object? Value, string? Error)> LastKnown = new();
        // This scope instance's most recent StmtHit id — "what just finished running", the
        // producing statement the NEXT observation's records are attributed to. -1 means
        // "no StmtHit observed yet" (the pending-baseline state).
        public int LastStatementId = -1;
    }

    private static readonly AlScopeFrames<CaptureState> _frames = new(() => new CaptureState());

    /// <summary>Reset before each top-level AL invocation whose locals should be captured.</summary>
    public static void Reset()
    {
        _series = new List<AlCapturedValue>();
        _frames.Clear();
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
    ///
    /// Returns the records THIS observation produced (empty when disabled, not the
    /// top-level scope, or nothing changed) so AlIterationTracker (#2056) can file them
    /// under the loop iteration they belong to; the same records are also appended to
    /// the flat series Collect() returns.
    /// </summary>
    public static IReadOnlyList<AlCapturedValue> OnStmtHit(NavMethodScope scope, int currentStatementNumber)
    {
        if (!Enabled) return Array.Empty<AlCapturedValue>();
        if (!scope.IsTopLevelCall) return Array.Empty<AlCapturedValue>();
        // NavMethodScope.ExitStatementNumber (int.MaxValue) is written directly by
        // Exit(), never passed to StmtHit by generated code — guarded defensively, same
        // reasoning as AlCoverageTracker.OnStmtHit's own guard.
        if (currentStatementNumber == int.MaxValue) return Array.Empty<AlCapturedValue>();

        AlNavNameReflection.EnsureInit();
        var scopeName = scope.ScopeName ?? "?";
        var state = _frames.GetOrPush(scope).State;
        bool isBaseline = state.LastStatementId < 0;
        // The statement that just finished: the producing statement for whatever this
        // observation shows, and the one whose write set forces records for the locals
        // it assigns even when their value did not change (see the file header). A
        // baseline can only carry values statement 0 itself produced before its hit, so
        // THAT is the producing statement for those.
        var previous = state.LastStatementId;
        var assigned = isBaseline ? null : AssignedBetween(scope.GetType(), previous, currentStatementNumber);
        var changed = DiffAndUpdate(scopeName, isBaseline ? currentStatementNumber : previous,
            NamedFields(scope), state.LastKnown, isBaseline, assigned);
        if (changed.Count > 0) (_series ??= new List<AlCapturedValue>()).AddRange(changed);
        state.LastStatementId = currentStatementNumber;
        return changed;
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
        // #2056: every scope exit — captured or not — also ends whatever loop instances
        // that scope still had open (AlIterationTracker self-gates on its own flag).
        IReadOnlyList<AlCapturedValue> changed = Array.Empty<AlCapturedValue>();
        // Only the test's own locals — not those of any procedure it calls, which get
        // their own (deeper) scope instances and their own Exit() traffic. IsTopLevelCall
        // (StackDepth == 2, decompiled and confirmed) is true exactly for the scope invoked
        // directly by the runner, i.e. server `execute`'s OnRun today.
        if (Enabled && scope.IsTopLevelCall)
        {
            AlNavNameReflection.EnsureInit();
            var scopeName = scope.ScopeName ?? "?";
            // Read BEFORE Exit()'s own body runs, so this is the real last-executed statement
            // index, not the int.MaxValue sentinel Exit() is about to write.
            var statementId = scope.StatementNumber;
            // This scope's frame ends here. A scope whose body never called StmtHit at all
            // (a degenerate empty trigger) has no frame: an empty last-known map makes
            // DiffAndUpdate report every field once, the pre-#2074 backstop.
            var frame = _frames.Pop(scope);
            var lastKnown = frame?.State.LastKnown ?? new Dictionary<string, (object?, string?)>();
            var assigned = AlScopeSyntaxResolver.Resolve(scope.GetType())?.Writes.TargetsOf(statementId);

            changed = DiffAndUpdate(scopeName, statementId, NamedFields(scope), lastKnown, isBaseline: false, assigned);
            if (changed.Count > 0) (_series ??= new List<AlCapturedValue>()).AddRange(changed);
        }
        AlIterationTracker.OnScopeExit(scope, changed);
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
    /// <paramref name="lastKnown"/> but nothing is returned — see OnStmtHit's doc comment
    /// for why the very first observation of a scope has no producing statement to credit
    /// — EXCEPT a CLR primitive/string that already holds a non-default value, which only
    /// statement 0's pre-hit part can have produced (a leading `for` loop's variable, see
    /// the file header); that one is returned, attributed to <paramref
    /// name="attributionStatementId"/>, which the caller passes as statement 0 itself.
    ///
    /// <paramref name="assigned"/> is the write set of the statement that just finished
    /// (AlWriteSetTable.TargetsOf; null at a baseline or when the scope has no syntax
    /// info): a field in it is returned even when its value did NOT change, because the
    /// statement executed and assigned it — the "one record per execution" the #2056
    /// contract asks for. Matched case-insensitively, as AL identifiers are. A field
    /// neither changed nor assigned is still skipped.
    /// </summary>
    internal static List<AlCapturedValue> DiffAndUpdate(
        string scopeName, int attributionStatementId,
        IEnumerable<(string Name, Func<object?> ReadField)> fields,
        Dictionary<string, (object? Value, string? Error)> lastKnown,
        bool isBaseline,
        IReadOnlySet<string>? assigned = null)
    {
        var changed = new List<AlCapturedValue>();
        foreach (var (name, readField) in fields)
        {
            var captured = CaptureField(scopeName, name, attributionStatementId, readField, out var raw);
            bool unchanged = lastKnown.TryGetValue(name, out var prev)
                && Equals(prev.Value, captured.Value) && prev.Error == captured.CaptureError;
            if (unchanged && !WasAssigned(assigned, name))
            {
                continue; // neither changed nor assigned since the last observation — no execution to report
            }
            lastKnown[name] = (captured.Value, captured.CaptureError);
            if (!isBaseline || (captured.CaptureError == null && AssignedBeforeFirstHit(raw)))
                changed.Add(captured);
        }
        return changed;
    }

    // The write set of the statement that just finished, plus the counted-loop variables
    // assigned between it and the statement about to run (AlLoopScopeTable.
    // LoopVariablesAssignedBefore) - null when the scope has no syntax info at all.
    private static IReadOnlySet<string>? AssignedBetween(Type scopeType, int previous, int current)
    {
        var syntax = AlScopeSyntaxResolver.Resolve(scopeType);
        if (syntax == null) return null;
        var writes = syntax.Writes.TargetsOf(previous);
        HashSet<string>? merged = null;
        foreach (var loopVariable in syntax.Loops.LoopVariablesAssignedBefore(current, previous))
        {
            merged ??= new HashSet<string>(writes, StringComparer.OrdinalIgnoreCase);
            merged.Add(loopVariable);
        }
        return merged ?? writes;
    }

    // AL identifiers are case-insensitive; the set from AlWriteSetTable already is, but a
    // caller-supplied set may not be, so fall back to a case-insensitive scan.
    private static bool WasAssigned(IReadOnlySet<string>? assigned, string name)
    {
        if (assigned == null || assigned.Count == 0) return false;
        if (assigned.Contains(name)) return true;
        foreach (var a in assigned)
            if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// True when a raw field value at the scope's FIRST observation proves an assignment
    /// already ran: AL locals start at their type's default, so a non-default CLR
    /// primitive or non-empty string cannot be the declared default. Deliberately
    /// limited to types whose default is unambiguous — a BC value-type wrapper's
    /// ToString() of its default may be non-empty, and reporting that as "produced by
    /// statement 0" would be a fake value (.claude/rules/loud-failures.md cuts both
    /// ways: never fabricate, never drop).
    /// </summary>
    private static bool AssignedBeforeFirstHit(object? raw) => raw switch
    {
        null => false,
        bool b => b,
        string s => s.Length > 0,
        byte or sbyte or short or ushort or int or uint or long or ulong => Convert.ToDecimal(raw) != 0m,
        float f => f != 0f,
        double d => d != 0d,
        decimal m => m != 0m,
        _ => false,
    };

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
        string scopeName, string name, int statementId, Func<object?> readField) =>
        CaptureField(scopeName, name, statementId, readField, out _);

    /// <summary>Same as the overload above, also handing back the raw CLR value that was
    /// read (null when the read threw) — DiffAndUpdate's baseline rule needs the raw
    /// value's TYPE, which the wire value no longer carries.</summary>
    internal static AlCapturedValue CaptureField(
        string scopeName, string name, int statementId, Func<object?> readField, out object? raw)
    {
        raw = null;
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
