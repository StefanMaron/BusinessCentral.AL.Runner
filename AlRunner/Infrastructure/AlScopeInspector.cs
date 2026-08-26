// AlScopeInspector — reads the AL locals of a LIVE NavMethodScope instance, for --dap's
// (#1642) "variables" request. Distinct from AlValueCapture (#1640): that one snapshots
// the top-level scope exactly once, at Exit(), after every statement has run. This one
// reads whichever scope instance a breakpoint pause handed it, at ANY point while a
// method is executing — a debugger needs the frame's state as of the moment execution
// stopped, not just the final state.
//
// This is safe to do "live" (the scope object is not done running) for the same reason
// a breakpoint pausing at StmtHit(N) is itself correct: BC calls StmtHit(N) BEFORE
// statement N's own side effect (see AlValueCapture's file header and
// AlDapSession's), so "paused at line L" means "every statement before L has
// completed, statement L has not started" — exactly what a debugger UI is supposed to
// show. Reading the scope's fields at that instant is reading real, settled state, not
// a mid-assignment tear.
using System.Reflection;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Infrastructure;

/// <summary>One AL local as seen at a live pause. <c>Value</c> is the wire-formatted
/// value (see AlValueWireFormat); <c>Readable</c> is false when EITHER reflection itself
/// failed to read the field OR the field read fine but the raw value's own ToString()
/// threw (issue #2051 — before this fix, a ToString() failure was silently flattened to
/// the same (Readable:true, Value:null) shape a genuinely-null AL local produces). The
/// NAME still appears in both failure modes (never silently dropped, see
/// .claude/rules/loud-failures.md), with an explicit marker instead of a value.</summary>
public readonly record struct AlScopeLocal(string Name, object? Value, bool Readable);

public static class AlScopeInspector
{
    /// <summary>
    /// Every AL local currently visible on <paramref name="scope"/> — the same
    /// [NavName]-tagged public instance field scan AlValueCapture.OnExit uses (via the
    /// shared AlNavNameReflection), but against ANY live scope instance rather than only
    /// at Exit(). A field that can't be reflected is reported with
    /// <c>Readable:false</c> rather than omitted, so a debugger UI shows "cannot read
    /// value" instead of the local silently vanishing from the Variables pane.
    /// </summary>
    public static List<AlScopeLocal> ReadLocals(NavMethodScope scope)
    {
        AlNavNameReflection.EnsureInit();
        var result = new List<AlScopeLocal>();
        foreach (var f in scope.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            var name = AlNavNameReflection.GetAlName(f);
            if (name == null) continue;
            result.Add(ReadField(name, () => f.GetValue(scope)));
        }
        return result;
    }

    /// <summary>
    /// Reads one AL local given a way to fetch its raw CLR value. Extracted from
    /// <see cref="ReadLocals"/> (mirroring AlValueCapture.CaptureField's extraction for
    /// #2043) so both failure modes issue #2051 names — a read that throws, and a
    /// ToString() that throws — are unit-testable without a real NavMethodScope:
    /// <paramref name="readField"/> is exactly <c>() =&gt; f.GetValue(scope)</c> in
    /// production, but a test can inject a throwing delegate directly. Neither failure
    /// mode is allowed to propagate — this feeds a live DAP "variables" response and must
    /// never crash a paused debug session.
    /// </summary>
    internal static AlScopeLocal ReadField(string name, Func<object?> readField)
    {
        object? raw;
        try { raw = readField(); }
        catch (Exception ex)
        {
            return new AlScopeLocal(name, $"<unreadable: {ex.GetType().Name}>", false);
        }
        var wireValue = AlValueWireFormat.ToWireValue(raw, out var captureError);
        if (captureError != null)
        {
            // Same marker-string / Readable:false convention as the read-throws case
            // above, so both failure modes render identically in the DAP Variables pane
            // instead of the ToString()-throws case silently collapsing to a genuinely-
            // null AL local's (Readable:true, Value:null) shape.
            return new AlScopeLocal(name, $"<unreadable: {captureError}>", false);
        }
        return new AlScopeLocal(name, wireValue, true);
    }
}
