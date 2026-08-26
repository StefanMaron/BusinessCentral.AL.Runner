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
/// value (see AlValueWireFormat); <c>Readable</c> is false only when reflection itself
/// failed to read the field — the NAME still appears (never silently dropped, see
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
            object? raw;
            try { raw = f.GetValue(scope); }
            catch (Exception ex)
            {
                result.Add(new AlScopeLocal(name, $"<unreadable: {ex.GetType().Name}>", false));
                continue;
            }
            result.Add(new AlScopeLocal(name, AlValueWireFormat.ToWireValue(raw), true));
        }
        return result;
    }
}
