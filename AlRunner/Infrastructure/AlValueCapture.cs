// AlValueCapture — the runtime side of --capture-values (issue #1640, second slice of
// the #1640 umbrella; --coverage was the first, #1922). The NavName reflection lookup
// and the wire-value conversion below are shared with --dap's live variable inspection
// (issue #1642) via AlNavNameReflection / AlValueWireFormat — see those files.
// Snapshots the AL locals of the
// TOP-LEVEL AL call (the codeunit trigger the runner invokes directly — server `execute`'s
// OnRun today) at the moment the scope is about to be disposed, via a Cecil-rewrite hook on
// Microsoft.Dynamics.Nav.Ncl.dll's NavMethodScope.Exit() — see NclCecilRewrite's Exit block.
//
// The prior "blocked on a mechanism for reading AL locals" framing (see #1640's original
// text) does not hold: BC's own AL compiler lifts every AL local to a PUBLIC instance
// field on the generated `*_Scope` class, tagged `[NavName("<AL name>")]` — confirmed by
// decompiling an emitted test DLL (DUMP_CS=1) and cross-checked against a live probe
// fixture during this issue's investigation. Reading those fields via reflection needs no
// new instrumentation and no pass over AL output.
//
// WHY Exit(), NOT StmtHit(int) — the design actually shipped here, after a wrong first
// attempt:
//
// BC's generated code calls StmtHit(N) BEFORE statement N's own side effect runs (decompile
// evidence: `StmtHit(3); this.msg = new NavText("after");`). A "snapshot on every StmtHit,
// keep the latest" design (the coverage hook's shape) is therefore always ONE STATEMENT
// STALE: at the LAST hit, the previous statement's effect is visible but the CURRENT
// statement's is not — a probe fixture caught this directly (asserted "after", the
// StmtHit-based prototype reported "before"). There is no StmtHit call after the final
// statement to correct for it.
//
// NavMethodScope.Run() (decompiled) is:
//   try { OnRun(); } catch (...) { ... } finally { Exit(); }
// and Exit() is:
//   internal void Exit() { statementNumber = int.MaxValue; ...; Dispose(); }
// So Exit() fires EXACTLY ONCE per scope, unconditionally (success or AL error — see
// Run()'s finally), strictly AFTER every statement in OnRun() — including its side
// effects — has completed, and STRICTLY BEFORE Dispose() (confirmed by decompile: Dispose()
// does not touch AL-declared fields, only Tree/session bookkeeping, so nothing is torn down
// before our hook runs). Prepending the capture call before Exit()'s own
// `statementNumber = int.MaxValue` line means AlCapturedValue.StatementId is the real last-
// executed statement index, not the ExitStatementNumber sentinel.
//
// "The scope is disposed when the AL method returns" (per the issue's own #1640 comment) is
// exactly why the capture must happen INSIDE Exit(), before Dispose() — by the time our C#
// caller's Invoke() returns, the scope object has already run through Dispose().
using System.Reflection;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Infrastructure;

/// <summary>One AL local's value, captured the instant its top-level scope's method body
/// finished (NavMethodScope.Exit(), before Dispose()). <c>StatementId</c> indexes the SAME
/// [SourceSpans] array AlCoverageTracker/AlCallStackCapture already decode, so a caller
/// that also wants the AL source line can resolve it via AlSourceSpanCodec.
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
    /// <summary>True only while a --capture-values run is executing. Gates OnExit; the
    /// Cecil-rewritten Exit() call is unconditional, this flag is not — same pattern as
    /// AlCoverageTracker.Enabled.</summary>
    public static volatile bool Enabled;

    // Single process-global slot, NOT per-scope: only the OUTERMOST AL call (IsTopLevelCall)
    // is captured (see the file header), and the runner invokes exactly one such call at a
    // time — RunFirstCodeunitOnRun's OnRun invocations run strictly sequentially, matching
    // the same single-slot assumption AlCallStackCapture already makes for the AL call stack.
    private static volatile List<AlCapturedValue>? _snapshot;

    /// <summary>Reset before each top-level AL invocation whose locals should be captured.</summary>
    public static void Reset() => _snapshot = null;

    /// <summary>The most recent snapshot, or an empty list if nothing was captured yet
    /// (--capture-values on but the top-level scope has no AL locals, or Exit() never
    /// fired — e.g. a compile/setup failure before any AL code ran). Never null so callers
    /// don't need a null-check.</summary>
    public static IReadOnlyList<AlCapturedValue> Collect() =>
        _snapshot ?? (IReadOnlyList<AlCapturedValue>)Array.Empty<AlCapturedValue>();

    /// <summary>
    /// Hook target for the Cecil-rewritten NavMethodScope.Exit() — public static, exactly
    /// (NavMethodScope), prepended before Exit()'s own body (see the file header for why
    /// that ordering matters). Must stay side-effect-free beyond the snapshot: it runs once
    /// per AL method invocation, capture-values or not.
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
        var values = new List<AlCapturedValue>();
        foreach (var f in scope.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            var name = AlNavNameReflection.GetAlName(f);
            if (name == null) continue;
            values.Add(CaptureField(scopeName, name, statementId, () => f.GetValue(scope)));
        }
        _snapshot = values;
    }

    /// <summary>
    /// Captures one AL local given a way to read its raw CLR value. Extracted from
    /// <see cref="OnExit"/> so the two failure modes issue #2043 names — a read that
    /// throws, and a ToString() that throws — are unit-testable without a real
    /// NavMethodScope: <paramref name="readField"/> is exactly <c>() =&gt;
    /// f.GetValue(scope)</c> in production, but a test can inject a throwing delegate
    /// directly. Neither failure mode is allowed to propagate out of this method (this is
    /// what OnExit calls, and OnExit itself may never throw — see the file header and
    /// AlValueCaptureErrorVisibilityTests).
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
