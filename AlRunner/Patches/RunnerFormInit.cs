// RunnerFormInit — the gate that decides whether a NavForm is allowed to really
// initialise itself.
//
// BACKGROUND
//   NclCecilRewrite collapses three NavForm methods to a bare `ret`:
//     CallInitializeComponentExtensionMethod, InitializeForm, RegisterSourceExpression.
//   They were neutered for the REPORT REQUEST-PAGE path: {Report}.RequestPage.
//   InitializeComponent walks them, and they touch skeleton-session state (the
//   PageExtensions list, Session.IsCompanyOpen, MasterPage.Expressions) that headless
//   mode leaves unset. The justification was "no AL-observable effect" — true at the
//   time, because no page was ever actually driven, only constructed as a side effect of
//   a report.
//
//   That is no longer true. RegisterSourceExpression is precisely how a page publishes
//   its control -> value bindings, and NavForm.SourceExpressions is the ONLY thing that
//   can resolve a control bound to a page variable rather than to a Rec field. A blanket
//   no-op means SourceExpressions is permanently null, so the TestPage path can never see
//   a control that is not a table field.
//
// WHAT CHANGED
//   The three methods are no longer emptied. They keep their original bodies behind a
//   guard: run for real only for forms the runner deliberately opted in, no-op for
//   everyone else. So the report request-page path behaves exactly as it did — byte for
//   byte, same early return — and only a page the TestPage machinery built and marked
//   gets BC's real initialisation.
//
//   Opting in per INSTANCE, rather than by page id or by "has real metadata", is
//   deliberate: it is the narrowest possible widening. A report's request page and a
//   TestPage over the same page id would still be treated differently, because what
//   matters is which one the runner is driving.
using System.Runtime.CompilerServices;

namespace AlRunnerV2.Patches;

public static class RunnerFormInit
{
    // Instance-keyed and weak: a form that goes out of scope must not be kept alive by
    // this gate, and form identity is the whole point (see above).
    private static readonly ConditionalWeakTable<object, object> _realInitForms = new();
    private static readonly object Marker = new();

    /// <summary>
    /// Opt <paramref name="form"/> into BC's real form initialisation. Called by the
    /// TestPage path immediately after constructing the page instance and before driving
    /// it, so the guard below is already true by the time BC reaches it.
    /// </summary>
    public static void MarkRealInit(object form)
    {
        if (form == null) return;
        _realInitForms.TryGetValue(form, out _);
        _realInitForms.Remove(form);
        _realInitForms.Add(form, Marker);
    }

    /// <summary>
    /// Cecil-injected guard at the top of NavForm.InitializeForm /
    /// CallInitializeComponentExtensionMethod / RegisterSourceExpression: true runs the
    /// original body, false returns immediately (the previous unconditional behaviour).
    /// Must never throw — it runs inside BC's own IL.
    /// </summary>
    public static bool ShouldRunRealFormInit(object form)
    {
        try { return form != null && _realInitForms.TryGetValue(form, out _); }
        catch { return false; }
    }
}
