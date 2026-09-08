using System;
using System.Reflection;
using AlRunner.Infrastructure;
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
    ///
    /// <para>Callers pass <c>-</c> and <c>+</c>. Do NOT "correct" these to the <c>=&gt;&lt;</c>
    /// / <c>=&lt;</c> a real client sends: those mean "nearest to the key the record currently
    /// holds", which is an anchor, not a first or last row — docs/page-rowset-triggers.md has
    /// the measurement and what it costs.</para>
    /// </summary>
    internal bool? RaiseOnFindRecord(string which)
    {
        if (!DeclaresRowsetTrigger("IsFindRecordTriggerDefined")) return null;
        var result = InvokeRecordTrigger("OnFindRecord", new[] { typeof(NavText) },
            new object[] { new NavText(which) });
        return result as bool? ?? throw NotDispatched("OnFindRecord", "RaiseOnFindRecordAsync(NavText)", result);
    }

    /// <summary>
    /// Run the page's own <c>OnNextRecord(Steps)</c>, or answer null when it declares none.
    ///
    /// <para><paramref name="steps"/> is +1 along the rowset and -1 back, in the page's own sort
    /// order — the only two values BC was observed to pass (corpus codeunit 60679, BC 28.4). The
    /// return is how far it actually moved, so 0 means an end of the rowset.</para>
    ///
    /// <para><c>setSize</c> stays 0. BC reads <c>IsNextRecordTriggerDefined</c> for a non-zero
    /// one and then makes its OWN routing decision; this caller has already made that decision
    /// from the same flag, so 0 keeps one decision rather than two — docs/page-rowset-triggers.md.</para>
    /// </summary>
    internal int? RaiseOnNextRecord(int steps)
    {
        if (!DeclaresRowsetTrigger("IsNextRecordTriggerDefined")) return null;
        var result = InvokeRecordTrigger("OnNextRecord", new[] { typeof(int), typeof(int) },
            new object[] { steps, 0 });
        return result as int? ?? throw NotDispatched("OnNextRecord", "RaiseOnNextRecordAsync(int,int)", result);
    }

    /// <summary>
    /// The page declared the trigger and the invocation produced no value, so its rowset went
    /// unconsulted. Answering from the SourceTable instead is the defect this file fixes,
    /// reappearing silently on whichever BC version changed the shape
    /// <see cref="InvokeRecordTrigger"/> resolves.
    /// </summary>
    private Exception NotDispatched(string trigger, string bcMember, object? result)
        => new AlRunner.Infrastructure.RunnerOutOfScopeException(
            $"NavForm.{bcMember}",
            $"page-rowset-trigger-not-dispatched — page {_form?.GetType().Name ?? "<unknown>"} declares {trigger}, but "
            + "invoking it produced "
            + (result == null ? "no value" : $"a {result.GetType().Name}")
            + $". The runner cannot serve this page's rowset without {trigger}, and answering "
            + "from the SourceTable instead would silently show the wrong rows. See "
            + "docs/page-rowset-triggers.md");

    /// <summary>
    /// Whether this page declares the trigger behind <paramref name="flag"/>, read from BC's own
    /// page metadata — the twelve <c>NCLMetaForm.Is&lt;Trigger&gt;Defined</c> properties.
    ///
    /// <para>This used to reflect over the compiled page class instead, because the flags
    /// answered false for a page that did declare the trigger (#3447). They answer truthfully
    /// now, so the runner and BC's own consumers of these flags decide from one value.</para>
    ///
    /// <para>No metaform is BC's own "not defined" — <c>NavForm.RaiseOnNextRecordAsync</c> reads
    /// exactly this pair the same way, <c>TryGetNclMetaForm(out m) &amp;&amp; m.IsNext…</c>.</para>
    /// </summary>
    private bool DeclaresRowsetTrigger(string flag)
    {
        var form = _form;
        if (form == null) return false;

        var tryGet = BcShape.FindMethod(form.GetType(), "TryGetNclMetaForm",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            surface: "page rowset triggers", member: "NavForm.TryGetNclMetaForm",
            detail: "the runner reads the page's declared triggers off its NCLMetaForm — see "
                  + "docs/page-rowset-triggers.md");
        if (tryGet == null)
            throw new AlRunner.Infrastructure.BcShapeGapException(
                "page rowset triggers", "NavForm.TryGetNclMetaForm",
                "BC no longer declares it, so the runner cannot reach the page metadata that "
                + "says whether this page serves its own rowset");

        var args = new object?[] { null };
        if (!(bool)tryGet.Invoke(form, args)! || args[0] is not object metaForm) return false;

        var prop = metaForm.GetType().GetProperty(flag,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new AlRunner.Infrastructure.BcShapeGapException(
                "page rowset triggers", $"NCLMetaForm.{flag}",
                "BC no longer declares this page-metadata flag, so the runner cannot tell whether "
                + "this page serves its own rowset");

        return (bool)prop.GetValue(metaForm)!;
    }
}
