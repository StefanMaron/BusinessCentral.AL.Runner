// RunnerServerFormRegistry — maps a NavTestPage instance to the real live NavForm
// (RunnerPageInstance.Form) TestPageFactory built it over.
//
// WHY
//   Real BC's NavTestPageBase.ServerForm resolves lazily via
//   `base.Session.Company.GetRegisteredForm(TestPage.FormHandle)` — a lookup against the
//   service tier's form registry. The runner has no such registry (no service tier), so
//   that call always misses and ServerForm was Cecil-rewritten (see NclCecilRewrite's
//   get_ServerForm rewrite) to fall back to `RuntimeHelpers.GetUninitializedObject(NavForm)`
//   — an object with valid vtable dispatch but every field, including its own
//   ITreeObject.Tree, left at its CLR default (null).
//
//   That is faithful for the surface the rewrite was written for (GetAutoFormatStringAsync,
//   whose rewritten body never dereferences `this`), but ServerForm also backs
//   TestPage.RunPageBackgroundTask and CurrPage.EnqueueBackgroundTask, both of which
//   construct a PageBackgroundTask / child NavSession *parented on* the form via its
//   ITreeObject.Tree. An uninitialised NavForm's Tree is null, and BC's own
//   TreeHandler(parent, hostObject) ctor throws "Parent.Tree cannot be null" the moment
//   anything is rooted under it — see issue #2514.
//
//   The runner already builds a REAL, correctly-initialised NavForm for every TestPage
//   whose page the runner compiled itself (RunnerPageInstance, via TestPageFactory) — the
//   same NavForm instance the TestPage's field/control lookups already go through. This
//   registry lets NavTestPageBase.get_ServerForm() hand back THAT form (with a real Tree)
//   instead of synthesising a second, tree-less one, for exactly the TestPage instances
//   where a live form exists. TestPages the runner could not build a live page object for
//   (no captured metadata, dependency page, etc.) still fall back to the uninitialised-form
//   stub — unchanged behaviour, and PBT calls on such a page were never faithfully
//   supportable anyway (there is nothing to reflect their triggers onto).
using System.Runtime.CompilerServices;

namespace AlRunner.Patches;

public static class RunnerServerFormRegistry
{
    // ConditionalWeakTable: entries die with the NavTestPage instance, no leak across tests.
    private static readonly ConditionalWeakTable<object, object> _map = new();

    internal static void Register(object testPage, object liveForm) => _map.AddOrUpdate(testPage, liveForm);

    internal static bool TryGet(object testPage, out object? liveForm) => _map.TryGetValue(testPage, out liveForm);

    /// <summary>
    /// Called from the Cecil-rewritten NavTestPageBase.get_ServerForm() (see NclCecilRewrite)
    /// in place of an unconditional RuntimeHelpers.GetUninitializedObject(NavForm) — returns
    /// the real live NavForm this TestPage was built over when one exists, and only falls
    /// back to an uninitialised stub (the pre-#2514 behaviour) when it does not. Public:
    /// resolved and called by name via Cecil/reflection from Ncl.dll's IL, not from C#.
    /// </summary>
    public static object ResolveOrCreateUninitialized(object testPageBase, Type navFormType)
    {
        if (_map.TryGetValue(testPageBase, out var live))
            return live;
        return System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(navFormType);
    }
}
