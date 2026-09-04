// RunnerTestPageState — tell a live TestPage when BC opened it.
//
// WHY THIS IS NEEDED AT ALL
//   BC attaches its ITestPage during Open(), from ClientSession.CreatePage. The runner has
//   no client session, so NavTestPageHandle.CreateTarget attaches the page at CONSTRUCTION
//   instead, and NclCecilRewrite removes `testPage = null` from InternalClear so the
//   attachment survives. That inverts what `testPage != null` means: in BC it means "open",
//   here it means "exists".
//
//   Two BC guards read through that:
//     NavTestPageBase.Open(ViewMode)  throws NavTestPageAlreadyOpenException if IsOpened
//     NavTestPageBase.Close()         forwards to testPage.Close() ONLY if IsOpened
//
//   The mock answered IsOpened() = false, which satisfied the first guard and silently
//   defeated the second: an AL test's Card.Close() never reached the runner's page, so a
//   row started with New() was never persisted at Close — it survived only if the variable
//   happened to be disposed later, which is after the test's own assertions have run. That
//   is why a part insert vanished while everything else about the part worked.
//
//   Answering true instead fixes Close and breaks Open, because the page is attached before
//   Open is ever called.
//
// WHAT THIS DOES
//   Makes "open" a real piece of state the page owns, so BOTH guards get a true answer:
//   the Cecil-rewritten NavTestPage.Open calls MarkOpened after BC's own Open has run, and
//   the page clears the flag when it closes. A first open passes the guard, a genuine
//   double-open still throws NavTestPageAlreadyOpenException, Close forwards, and reopening
//   after a Close works.
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Dynamics.Nav.Types.Exceptions;

namespace AlRunner.Patches;

public static class RunnerTestPageState
{
    private static FieldInfo? _testPageField;

    /// <summary>
    /// Mark the ITestPage attached to <paramref name="navTestPage"/> as open. Called from
    /// the rewritten NavTestPage.Open, immediately after NavTestPageBase.Open has run its
    /// already-open guard. Must never throw — it runs inside BC's own IL.
    ///
    /// <paramref name="viewMode"/> is what distinguishes OpenNew() from OpenEdit():
    /// <c>ALOpenNew()</c> is nothing but <c>Open(ViewMode.Create)</c>, and in BC the row it
    /// starts comes from the client the runner deliberately does not have. So the row has to
    /// be started here — otherwise OpenNew() opened an ordinary page positioned on nothing,
    /// every SetValue went into a record that was never inserted, and the test read a table
    /// with no new row in it.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void MarkOpened(object navTestPage, Microsoft.Dynamics.Nav.Types.Metadata.ViewMode viewMode)
    {
        try
        {
            if (navTestPage == null) return;
            _testPageField ??= FindTestPageField(navTestPage.GetType());
            if (_testPageField?.GetValue(navTestPage) is not LiveNavTestPage live) return;
            // Opening a page is a commit point. BC reaches one because the page opens inside a
            // new TRANSACTION WORLD, and TransactionManager.BeginTransactionWorld commits the
            // active transaction on entering one (CommitImpl(commit: true)). The runner opens
            // the page in-process without that machinery, so the commit has to be made here —
            // otherwise an asserterror on a page operation rolls back the test's own setup,
            // which real BC has already committed by then.
            AlRunner.Patches.RecordPatches.MarkCommitPoint();
            live.MarkOpened(viewMode);
            // Before anything else reads the page: OnOpenPage is where a page establishes what
            // it is looking at — the singleton buffer it fetches or creates for the current
            // user, the filter it narrows to its caller's context.
            live.RaiseOnOpenPage();
            // Issue #2677: reach every declared subpage PART eagerly, here — right after the
            // host's own OnOpenPage, before the host's first row is found below. This is
            // what makes a FactBox nobody's AL ever references still get its own OnOpenPage
            // (and, once the host's row is found, its OnAfterGetRecord/OnAfterGetCurrRecord
            // via LiveNavTestPage.Loaded's refresh) — matching corpus PR
            // StefanMaron/BusinessCentral.AL.Language.Tests#141's measured order
            // (HostOpen;PartOpen;HostAGCR;PartAGCR) on all 8 BC legs.
            live.EagerlyBuildParts();
            if (viewMode == Microsoft.Dynamics.Nav.Types.Metadata.ViewMode.Create)
                live.InsertEmptyRow(beforeCurrent: true);
            else if (live.Record != null && AlRunner.Patches.RunnerTestClientSession.IsUnpositioned(live.Record))
                // A real client positions on the first row (or the implicit new-row line, if
                // the view has none — see LiveNavTestPage.MoveFirst) the instant the page
                // opens, before any AL code runs. Without this, a page opened on an empty view
                // sat on nothing until an AL test called First()/Next() itself — and code that
                // never does that (issue #2392: Base App's ApprovalCommentsHandler writes a
                // field straight after Trap(), no navigation call of its own) wrote into a
                // record that had never been positioned at all.
                //
                // Guarded on Record != null: a page with no SourceTable (the StandardDialog
                // shape, issue #2007) has no rowset to position at all, and MoveFirst() refuses
                // that case by name (RequireRecord) rather than silently doing nothing.
                //
                // Guarded on IsUnpositioned too, same reason as RunnerTestClientSession.GetPage
                // (corpus CU60848 RunModal_OpensOnTheRecordSetByTheCaller): a record the caller
                // already positioned on a specific row must not be silently reset to the
                // table's own first row.
                live.MoveFirstDuringOpen();
        }
        // Issue #2677: EagerlyBuildParts + Loaded's own part-refresh can now run AL trigger
        // code (a subpage part's OnAfterGetRecord/OnAfterGetCurrRecord, and whatever that
        // enqueues) as part of MoveFirst — code that previously never ran this early, so a
        // genuine AL error raised in it (NavBaseException, BC's own AL-error hierarchy —
        // e.g. the read-only-session write refusal issue #2514/#2650 raises) never had a
        // path to this catch before. A real AL error belongs on the caller's stack, exactly
        // as it would be on a real service tier's Open() — swallowing it here would be
        // precisely the silent-default `.claude/rules/loud-failures.md` forbids. Only a
        // NON-NavBaseException (a runner-internal/reflection failure — the ORIGINAL reason
        // this catch exists, per the type comment above) still means "a page that cannot be
        // marked simply behaves as it did before".
        catch (Exception ex) when (ex is not NavBaseException)
        {
        }
    }

    private static FieldInfo? FindTestPageField(Type type)
    {
        for (var t = type; t != null; t = t.BaseType)
        {
            var field = t.GetField("testPage", BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null) return field;
        }
        return null;
    }
}
