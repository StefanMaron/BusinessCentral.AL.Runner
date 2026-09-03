// RunnerChildSessionOpen — fast-path for NavSession.Open() on a CHILD session.
//
// WHY
//   Real BC's NavSession.Open(bool, byte[], bool) is a full service-tier session bootstrap:
//   SQL database-version checks, license validation, `Permissions = new
//   NavUserPermissions(Authenticator.User, this)`, `company = new NavCompany(this, null,
//   evaluationCompany: false)`, culture/personalization lookups, and more — none of which
//   the runner's in-process, no-SQL skeleton tenant can answer, because none of it presupposes
//   anything beyond "a real service tier is behind this session".
//
//   The runner's OWN root session sidesteps every bit of that by never calling Open() at all
//   — BcRuntime builds it via RuntimeHelpers.GetUninitializedObject and field-pokes just the
//   state AL-visible surfaces need. A page background task's synchronous child session (the
//   shape BC's own test framework always takes; see NavForm.EnqueueBackgroundTask /
//   NavTestPage.ALRunPageBackgroundTask) does NOT get that treatment — it is a REAL NavSession
//   built through NavSession's own constructor and then really `.Open()`ed
//   (NavChildSessionTaskRuntime&lt;T&gt;.RunAsync -> childSession.Open()), which walked
//   straight into `NavUserPermissions..ctor` dereferencing state (session.Company) the child
//   session had not been given yet — see issue #2514.
//
//   For a runner child session, "open" has exactly one honest meaning: reuse whatever the
//   PARENT session (which the runner's normal machinery already made to work) resolved for
//   Permissions and Company, because there is no separate SQL scope, license state or
//   personalization layer here to make them meaningfully diverge. That is also the same
//   answer BC's own Open() gives for TenantLicenseState and RegionalSettings on a child
//   session (`if (IsChildSession) { cultureSettings = ParentSession.RegionalSettings; ... }`)
//   — this fast-path just extends that "inherit from parent" treatment to Permissions and
//   Company too, instead of trying to rebuild them from a service tier that is not there.
using System.Reflection;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Patches;

public static class RunnerChildSessionOpen
{
    private static readonly FieldInfo? _fHasBeenOpened =
        typeof(NavSession).GetField("hasBeenOpened", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly FieldInfo? _fCompany =
        typeof(NavSession).GetField("company", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly PropertyInfo? _pPermissions =
        typeof(NavSession).GetProperty("Permissions", BindingFlags.Public | BindingFlags.Instance);
    private static readonly FieldInfo? _fPermissionsBacking =
        typeof(NavSession).GetField("<Permissions>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);

    /// <summary>
    /// Called from the Cecil-inserted prefix at the top of NavSession.Open(bool, byte[], bool).
    /// Returns true when it fully handled the open (a child session — the caller must return
    /// immediately without running BC's real body), false when the real body must run (every
    /// session that is NOT a child session; this fast path never touches those).
    /// </summary>
    public static bool TryFastOpen(NavSession session)
    {
        if (!session.IsChildSession) return false;

        if (_fHasBeenOpened != null) _fHasBeenOpened.SetValue(session, true);

        var parent = session.ParentSession;
        if (parent != null)
        {
            if (_fCompany != null)
            {
                var parentCompany = _fCompany.GetValue(parent);
                if (parentCompany != null) _fCompany.SetValue(session, parentCompany);
            }
            var parentPermissions = _pPermissions?.GetValue(parent);
            if (parentPermissions != null)
            {
                // Permissions has a private setter — go through the backing field directly,
                // same pattern as the rest of the skeleton (FieldPoke.SetInstance elsewhere).
                if (_fPermissionsBacking != null) _fPermissionsBacking.SetValue(session, parentPermissions);
            }
        }
        return true;
    }
}
