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
        if (!DeclaresRowsetTrigger("OnFindRecord", new[] { typeof(NavText) })) return null;
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
    /// <para><c>setSize</c> must stay 0. A non-zero value makes BC consult the
    /// <c>IsNextRecordTriggerDefined</c> flag this runner does not populate (#3447) and fall
    /// back to a different platform call than the caller's — docs/page-rowset-triggers.md.</para>
    /// </summary>
    internal int? RaiseOnNextRecord(int steps)
    {
        if (!DeclaresRowsetTrigger("OnNextRecord", new[] { typeof(int) })) return null;
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
    /// Whether this page overrode <paramref name="trigger"/>, in either flavour the AL compiler
    /// emits.
    ///
    /// <para><c>DeclaredOnly</c> is load-bearing: both triggers are <c>NavForm</c> virtuals whose
    /// BASE bodies are the platform find and step, so a plain <c>GetMethod</c> resolves on every
    /// page and every page would look as though it declared them. BC asks page metadata instead;
    /// that answers FALSE here for a page that does declare the trigger (#3447), which is why
    /// this asks the compiled class — see docs/page-rowset-triggers.md.</para>
    /// </summary>
    private bool DeclaresRowsetTrigger(string trigger, Type[] types)
    {
        const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic
                                    | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        for (var t = _form?.GetType(); t != null && t != typeof(NavForm); t = t.BaseType)
            foreach (var name in new[] { trigger, trigger + "Async" })
                if (BcShape.FindMethod(t, name, Declared,
                        surface: "page rowset triggers", member: $"{t.Name}.{name}",
                        detail: "the runner asks whether this page overrode the trigger, and two "
                              + "declarations of one name on one type leave no way to say which "
                              + "body serves the rowset — see docs/page-rowset-triggers.md",
                        types: types) != null)
                    return true;

        return false;
    }
}
