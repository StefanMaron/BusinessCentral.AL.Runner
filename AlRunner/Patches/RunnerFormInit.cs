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

namespace AlRunner.Patches;

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

    /// <summary>
    /// Cecil-injected guard on NavForm.GetMasterPage specifically — deliberately WIDER than
    /// ShouldRunRealFormInit.
    ///
    /// The instance opt-in above covers pages the runner itself constructs for a TestPage.
    /// It does not cover a page BC opens on AL's own behalf — `SomePage.RunModal()` inside a
    /// trigger — because the runner never sees that instance. Such a form got a null
    /// MasterPage, and BC's modal-page handler dispatch reads
    /// form.MasterPage.PageProperties.PageType (NavTestExecution.FindPageType) before it
    /// looks up a handler, so every modal page raised a NullReferenceException instead of
    /// reaching its [ModalPageHandler]. Thirty Pageworks tests declare HandlerFunctions.
    ///
    /// The condition is "the runner compiled this page, so it HAS metadata to build a real
    /// MasterPage from" — which is exactly when the lookup can succeed. A page with no
    /// captured XML still returns null, as before, rather than throwing from the loader.
    ///
    /// Request pages stay excluded: that is the path the original blanket no-op existed to
    /// protect, it is keyed by report rather than page id, and nothing here needs it.
    /// </summary>
    public static bool ShouldResolveMasterPage(object form)
    {
        try
        {
            if (form == null) return false;
            if (ShouldRunRealFormInit(form)) return true;
            if (form is not Microsoft.Dynamics.Nav.Runtime.NavForm navForm) return false;
            if (navForm.IsRequestPage) return false;
            return AlPageMetadataRegistry.TryGet(navForm.FormId, out _);
        }
        catch { return false; }
    }

    /// <summary>
    /// Cecil-injected guard on NavForm.RegisterSourceExpression — as wide as
    /// ShouldResolveMasterPage, and for the same reason.
    ///
    /// The instance opt-in is too narrow here for exactly the case it was designed around.
    /// A page AL opens with <c>SomePage.RunModal()</c> is an instance the runner never
    /// constructed, so it is never marked — yet the test's [ModalPageHandler] is handed a
    /// TestPage over that very form and is expected to drive it. Registration is what
    /// publishes a page's control -> value bindings, so without it the binding table is
    /// empty and EVERY control bound to a page variable rather than a Rec field is
    /// unresolvable on a modal page. That is how a mode selector above a picker's list —
    /// ordinary AL, and what Pageworks' InsertPicker does — surfaced as an out-of-scope
    /// control id inside a handler.
    ///
    /// Widening only this one of the three guarded methods is deliberate. Registration is a
    /// pure "record this binding" step against a MasterPage the wider gate has already
    /// admitted; InitializeForm and CallInitializeComponentExtensionMethod are the ones that
    /// reach into skeleton-session state, and they stay on the narrow instance opt-in.
    /// Request pages stay excluded — that is the path the original blanket no-op protected.
    /// </summary>
    public static bool ShouldRegisterSourceExpressions(object form) => ShouldResolveMasterPage(form);
}
