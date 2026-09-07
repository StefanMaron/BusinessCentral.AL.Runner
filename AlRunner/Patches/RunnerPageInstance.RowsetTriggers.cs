using System;
using System.Reflection;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Patches;

// A page's OnFindRecord/OnNextRecord triggers decide which rows the client shows — issue #3439.
// See docs/page-rowset-triggers.md for what BC's own client does with them, what the corpus
// measured on a real service tier, and why navigation here is not one trigger call per step.
internal sealed partial class RunnerPageInstance
{
    /// <summary>
    /// Run the page's own <c>OnFindRecord(Which)</c>, or answer null when it declares none.
    ///
    /// <para>Null and false are different answers and the caller must keep them apart: false is
    /// "the page looked and there is no such row", null is "this page has no such trigger, do
    /// the platform find yourself". Collapsing them would make every ordinary page report an
    /// empty rowset.</para>
    /// </summary>
    internal bool? RaiseOnFindRecord(string which)
    {
        if (!DeclaresRowsetTrigger("OnFindRecord")) return null;
        var result = InvokeRecordTrigger("OnFindRecord", new[] { typeof(NavText) },
            new object[] { new NavText(which) });
        return result as bool?;
    }

    /// <summary>
    /// Run the page's own <c>OnNextRecord(Steps)</c>, or answer null when it declares none.
    ///
    /// <para><paramref name="steps"/> is +1 to move one row along the rowset and -1 to move one
    /// back, in the page's own sort order — the only two values BC was observed to pass (corpus
    /// codeunit 60679, BC 28.4). The trigger answers how far it actually moved, so 0 means it
    /// did not move and the caller is at an end of the rowset.</para>
    ///
    /// <para>The <c>setSize</c> argument BC's <c>RaiseOnNextRecordAsync</c> takes is a viewport
    /// size, and 0 is passed on purpose: a non-zero value asks BC to re-check its own
    /// <c>IsNextRecordTriggerDefined</c> metadata flag and, when that is false, fall back to
    /// <c>SafeSourceTable.NextAsync(steps, setSize)</c> — a DIFFERENT platform call from the
    /// <c>ALNextAsync(steps)</c> the caller would otherwise make, and reached through a flag
    /// this runner does not populate (see <see cref="DeclaresRowsetTrigger"/>). Deciding here
    /// instead leaves the no-trigger path exactly as it was.</para>
    /// </summary>
    internal int? RaiseOnNextRecord(int steps)
    {
        if (!DeclaresRowsetTrigger("OnNextRecord")) return null;
        var result = InvokeRecordTrigger("OnNextRecord", new[] { typeof(int), typeof(int) },
            new object[] { steps, 0 });
        return result as int?;
    }

    /// <summary>
    /// Whether this page overrode <paramref name="trigger"/>, in either the sync or the async
    /// flavour the AL compiler emits.
    ///
    /// <para><c>DeclaredOnly</c> is what makes this a real question. <c>OnFindRecord</c> and
    /// <c>OnNextRecord</c> are virtuals on <c>NavForm</c> whose BASE bodies are the platform
    /// find and the platform step (<c>SafeSourceTable.ALFind(which)</c> /
    /// <c>ALNext(steps)</c>), so an ordinary <c>GetMethod</c> always succeeds and an invocation
    /// always returns a plausible answer whether or not the page overrode anything — every page
    /// would look as though it declared both.</para>
    ///
    /// <para>BC answers this question from page metadata instead, through
    /// <c>NCLMetaForm.IsFindRecordTriggerDefined</c> / <c>IsNextRecordTriggerDefined</c>, and
    /// that is not usable here: measured on the issue's own reproducer, a page whose compiled
    /// class carries an <c>OnFindRecord</c> override reads <c>IsFindRecordTriggerDefined</c>
    /// FALSE, because the runner's page metadata does not record the page's triggers. That is a
    /// gap of its own (#3447) and not this fix's to close — the compiled class is the authority
    /// on what the page declared, and it is the same authority the invocation then dispatches
    /// against.</para>
    /// </summary>
    private bool DeclaresRowsetTrigger(string trigger)
    {
        const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic
                                    | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        for (var t = _form?.GetType(); t != null && t != typeof(NavForm); t = t.BaseType)
            if (t.GetMethod(trigger, Declared) != null || t.GetMethod(trigger + "Async", Declared) != null)
                return true;

        return false;
    }
}
