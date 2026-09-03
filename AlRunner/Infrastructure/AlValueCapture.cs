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
// DIFF PLUS WRITE SET ON EVERY OBSERVATION:
//
// A field earns a record when its value (or capture error) changed since the last
// observation, or when the statement that just finished assigns it (its write set from
// the AL syntax, AlWriteSetModel.cs), changed or not. That is the full-fidelity contract
// agreed on SShadowS/ALchemist#1 for #2074/#2056: `x := 5` while x was 5 is a record. A
// field neither changed nor assigned gets nothing.
//
// PER SCOPE INSTANCE:
//
// The last-known map and last statement id live in a frame per NavMethodScope instance
// (AlScopeFrames); a global pair diffed a callee's locals against the caller's same-named
// ones. IsTopLevelCall is true for every scope of the run's root object (StackDepth only
// grows across an application-object boundary, see AlDapSession.cs), so all are captured.
//
// THE FIRST OBSERVATION IS A BASELINE, NEVER EMITTED, except a loop variable:
//
// The first StmtHit fires before any statement ran, so every field is at its default and
// nothing produced that state. Except that BC assigns a for/foreach variable BEFORE the
// loop statement's own hit, so at a `for` statement's hit (leading or not) its loop
// variable is read and attributed to the loop statement itself, whatever the value; every
// other field observed there is the previous statement's effect. A statement before the
// loop that assigns the same variable is observed once, folded into that record.
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

    // One ordered series for the whole invocation; diff state is per scope instance.
    private static volatile List<AlCapturedValue>? _series;

    private sealed class CaptureState
    {
        // Last observed (value, captureError) per local of this scope instance.
        public readonly Dictionary<string, (object? Value, string? Error)> LastKnown = new();
        // The statement that just finished; -1 until the first StmtHit (baseline pending).
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
    /// Returns the records this observation produced (also appended to the series), so
    /// AlIterationTracker can file them under the right iteration.
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
        var previous = state.LastStatementId;
        var syntax = AlScopeSyntaxResolver.Resolve(scope.GetType());
        var fields = NamedFields(scope);

        // A for/foreach statement's own hit: its loop variable was just assigned by it
        // (see the file header), the rest is the previous statement's effect.
        var headerVariable = syntax?.Loops.LoopVariableOfHeader(currentStatementNumber);
        List<AlCapturedValue> changed;
        if (headerVariable != null)
        {
            var loopField = fields.Where(f => string.Equals(f.Name, headerVariable, StringComparison.OrdinalIgnoreCase)).ToList();
            var rest = fields.Where(f => !string.Equals(f.Name, headerVariable, StringComparison.OrdinalIgnoreCase));
            changed = DiffAndUpdate(scopeName, previous, rest, state.LastKnown, isBaseline,
                isBaseline ? null : AssignedBetween(syntax, previous, currentStatementNumber));
            changed.AddRange(DiffAndUpdate(scopeName, currentStatementNumber, loopField, state.LastKnown, isBaseline: false,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { headerVariable }));
        }
        else
        {
            changed = DiffAndUpdate(scopeName, previous, fields, state.LastKnown, isBaseline,
                isBaseline ? null : AssignedBetween(syntax, previous, currentStatementNumber));
        }
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
        // Every scope exit also ends its open loop instances (AlIterationTracker self-gates).
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
            // No frame (a body that never called StmtHit): an empty map reports every field once.
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
    /// A field in <paramref name="assigned"/> (the finished statement's write set) is
    /// returned even when unchanged; a field neither changed nor assigned is skipped.
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
            var captured = CaptureField(scopeName, name, attributionStatementId, readField);
            bool unchanged = lastKnown.TryGetValue(name, out var prev)
                && Equals(prev.Value, captured.Value) && prev.Error == captured.CaptureError;
            if (unchanged && !WasAssigned(assigned, name))
            {
                continue; // neither changed nor assigned since the last observation — no execution to report
            }
            lastKnown[name] = (captured.Value, captured.CaptureError);
            if (!isBaseline) changed.Add(captured);
        }
        return changed;
    }

    // The finished statement's write set plus loop variables assigned before `current`.
    private static IReadOnlySet<string>? AssignedBetween(AlScopeSyntax? syntax, int previous, int current)
    {
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

    // AL identifiers are case-insensitive; a caller-supplied set may not be.
    private static bool WasAssigned(IReadOnlySet<string>? assigned, string name)
    {
        if (assigned == null || assigned.Count == 0) return false;
        if (assigned.Contains(name)) return true;
        foreach (var a in assigned)
            if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
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
