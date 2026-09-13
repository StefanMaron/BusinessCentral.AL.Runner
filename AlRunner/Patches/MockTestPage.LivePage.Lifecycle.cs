// LiveNavTestPage: open/teardown lifecycle and static editability.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;
using Microsoft.Dynamics.Nav.Types.Data;
using Microsoft.Dynamics.Nav.Types.Exceptions;

namespace AlRunner;
internal partial class LiveNavTestPage
{
    // Whether BC has opened this page. Set by the Cecil-rewritten NavTestPage.Open (via
    // RunnerTestPageState) and cleared on close.
    //
    // This has to be real state rather than a constant, because BOTH of BC's guards read
    // it and they want opposite answers at different moments:
    //   NavTestPageBase.Open()  throws NavTestPageAlreadyOpenException when it is true
    //   NavTestPageBase.Close() forwards to this class ONLY when it is true
    // In BC the two never conflict, because the page is attached during Open. The runner
    // attaches at construction (NavTestPageHandle.CreateTarget) and NclCecilRewrite keeps
    // that attachment across InternalClear, so a constant false silently disabled Close —
    // a row started with New() was then never persisted at Close, only at Dispose, which
    // is after the test's assertions have already read the table. See RunnerTestPageState.
    private bool _opened;

    // Set when an unhandled error propagates out of the page's own record-positioning
    // trigger (OnAfterGetRecord) while this TestPage is already open — see Loaded() below.
    //
    // Measured against a real BC service tier (27.5, 28.3, 28.4; issue #2656): an unhandled
    // error raised there tears down the TestPage's underlying client session. Every
    // subsequent call on the SAME TestPage variable then raises BC's own
    // "The TestPage is not open." — not the trigger's own error text — including the
    // navigation call itself, Close(), and a plain field read. Deliberately distinct from
    // _opened (which BC's own NavTestPageBase.Open()/Close() guards read): a torn-down page
    // must still make Close() forward into this class (real BC's Close() THROWS after
    // teardown, it does not silently no-op the way it would for a page that was simply never
    // opened), so _opened stays true and this flag alone gates the refusal.
    //
    // This is NOT a blanket "any unhandled trigger error tears the page down" rule — measured
    // the same way, an unhandled error from OnValidate (field validation) or OnAction
    // (action invocation) propagates with its own error text and leaves the page open. Only
    // Loaded() (the record-positioning trigger) sets this flag.
    private bool _tornDown;

    // Set only around the page-construction-time initial positioning call (MarkOpened /
    // RunnerTestClientSession.GetPage's own MoveFirst()). MarkOpened's caller wraps it in a
    // blanket `catch { }` that would swallow whatever Loaded() throws there; GetPage's is not
    // similarly guarded on the runner side (its caller is precompiled BC dispatch via
    // TestClientProxy<ITestPage>.Proxy, not audited here). Either way, teardown must not apply
    // during this call: the page never finished a first successful position, so treating a
    // failure there as "the page tore down" would leave every LATER, otherwise-unrelated call
    // on a freshly-adopted page wrongly answering "The TestPage is not open." -- for MarkOpened
    // specifically, that would follow a failure that never became AL-visible in the first
    // place (a pre-existing, separate gap: real BC's OpenView() propagates that first row's own
    // trigger error, catchable by asserterror, rather than swallowing it -- not this issue's
    // scope).
    private bool _suppressTeardownOnLoad;

    /// <summary>
    /// The page-construction-time initial positioning call -- see _suppressTeardownOnLoad.
    /// </summary>
    internal bool MoveFirstDuringOpen()
    {
        _suppressTeardownOnLoad = true;
        try { return MoveFirst(); }
        finally { _suppressTeardownOnLoad = false; }
    }

    /// <summary>
    /// Run the "a row became the page's current row" sequence for the row the page is ALREADY
    /// on, without moving the cursor — <see cref="Loaded"/>'s OnAfterGetRecord /
    /// OnAfterGetCurrRecord, the xRec before-image, and the linked-part refresh.
    ///
    /// Needed because a page handed to a [ModalPageHandler] / [PageHandler] is constructed
    /// already-open by BC's ShowDialog, so RunnerTestPageState.MarkOpened — the code that runs
    /// the open sequence for a page the AL test opened itself — never runs on that path.
    /// RunnerTestClientSession.GetPage compensated with MoveFirstDuringOpen, but only for a
    /// record nothing had positioned yet: a caller that opened the page ON a specific row
    /// (<c>PAGE.RunModal(id, Rec)</c>) must not have that row silently re-queried away (corpus
    /// CU60848 RunModal_OpensOnTheRecordSetByTheCaller). That guard is right about the CURSOR
    /// and wrong about the TRIGGERS: the row-load triggers belong to every row a page shows,
    /// however it came to be on it.
    ///
    /// Measured on Base Application page 403 "Purchase Order Statistics", whose totals are
    /// computed in RefreshOnAfterGetRecord() off OnAfterGetRecord and NOT in OnOpenPage: opened
    /// modally on a caller-positioned Purchase Header, it received OnOpenPage (raised by BC's
    /// own OpenForm inside RunnerModalDispatch.FormRunModal) but never OnAfterGetRecord, so
    /// every total it showed was its type default. See issue #2797.
    ///
    /// Shares MoveFirstDuringOpen's teardown suppression for the same reason: this is the
    /// page-construction-time row load, not a navigation call the AL test made, so an AL error
    /// raised in the trigger must propagate as itself rather than being converted into BC's
    /// "The TestPage is not open."
    /// </summary>
    internal bool MarkRowLoadedDuringOpen()
    {
        _suppressTeardownOnLoad = true;
        try { return Loaded(found: true); }
        finally { _suppressTeardownOnLoad = false; }
    }

    // Real BC's own exception for this ("The TestPage is not open.") is not part of the
    // runner's own type surface — construct BC's own NavNCLDialogException (the same
    // AL-catchable-by-asserterror mechanism every other faithful platform error in this file
    // uses; see e.g. HelperShims.MakeNavDrilldownActionNotSupportedException) with BC's exact
    // wording so `asserterror` + Assert.ExpectedError('The TestPage is not open') behaves the
    // same here as against a real service tier.
    private static System.Exception MakeTestPageNotOpenException(System.Exception? original = null)
    {
        var t = System.Type.GetType(
            "Microsoft.Dynamics.Nav.Types.Exceptions.NavNCLDialogException, Microsoft.Dynamics.Nav.Types");
        const string msg = "The TestPage is not open.";
        if (t != null)
        {
            var ctor = t.GetConstructor(new[] { typeof(string) });
            if (ctor != null)
            {
                var ex = (System.Exception)ctor.Invoke(new object[] { msg });
                // Not AL-visible (asserterror / GetLastErrorText only see the outer message,
                // matching real BC) -- carried so the runner can still NAME what actually
                // failed inside the trigger. Issue #3189: this used to be written under a key
                // nothing read, which made 132 Tests-SMB failures report a message with no
                // cause anywhere in the run. Infrastructure.MaskedTriggerErrorDiagnosis reads
                // it into the failure's diagnosis line, and MissingTestDataDiagnosis walks
                // through it so the mask does not hide ITS evidence either.
                if (original != null)
                    ex.Data[Infrastructure.MaskedTriggerErrorDiagnosis.ConvertedErrorDataKey] = original;
                return ex;
            }
        }
        // Same contract on the fallback path: the converted error is reachable both as the
        // InnerException and under the key the diagnosis reads, so which construction path ran
        // cannot change what the failure reports.
        var fallback = new System.InvalidOperationException(msg, original);
        if (original != null)
            fallback.Data[Infrastructure.MaskedTriggerErrorDiagnosis.ConvertedErrorDataKey] = original;
        return fallback;
    }

    /// <summary>
    /// Record that BC opened this page, in <paramref name="viewMode"/>.
    ///
    /// The mode is what <c>TestPage.Editable()</c> answers from. Real BC reports the page's
    /// STATIC editability there — the mode it was opened in, combined with the page's own
    /// <c>Editable</c> property — not whatever <c>CurrPage.Editable(…)</c> last set from a
    /// row trigger (corpus CU60687
    /// CurrPageEditable_TestPageGetterIgnoresTheRuntimeToggle, validated against a real
    /// service tier: a page whose OnAfterGetRecord flips CurrPage.Editable(false) still reads
    /// back Editable() = true). The live per-CONTROL properties are the mechanism that does
    /// follow the row; these are two different mechanisms and BC surfaces both.
    ///
    /// The page's declared Editable is read HERE, before OnOpenPage runs, so a runtime
    /// toggle cannot have moved it yet.
    /// </summary>
    internal void MarkOpened(Microsoft.Dynamics.Nav.Types.Metadata.ViewMode viewMode)
    {
        _opened = true;
        _staticEditableOverride = viewMode != Microsoft.Dynamics.Nav.Types.Metadata.ViewMode.View
                                  && DeclaredPageEditable;
    }

    // Set only by MarkOpened — i.e. only for a page the TEST opened, where the open MODE is
    // what decides editability. Null everywhere else, which is why _staticEditable below has
    // to have an answer of its own rather than a default.
    private bool? _staticEditableOverride;

    // The host, for a subpage part. A part is reached through its host and is editable only
    // if the host is; see _staticEditable.
    private LiveNavTestPage? _editabilityHost;

    /// <summary>
    /// The page's STATIC editability: the open mode (when the test opened it), narrowed by the
    /// page's own declared <c>Editable</c>, and by its host's when it is a part.
    ///
    /// This used to be a plain field defaulting to true, written only by MarkOpened — and
    /// MarkOpened only ever runs for a page the test opens ITSELF. Every page BC hands to a
    /// [ModalPageHandler] / [PageHandler], and every subpage part, therefore reported itself
    /// editable no matter what it declared: an <c>Editable = false</c> page opened through
    /// RunModal answered TestPage.Editable() = true, and (once the new-row line landed) would
    /// have offered a blank line to type into on a page nobody can type into.
    ///
    /// Computed rather than snapshotted because a part is built lazily, on first access, and
    /// nothing orders that against its host being opened.
    /// </summary>
    private bool _staticEditable
        => TestPageNewRowLineRule.ResolveStaticEditable(
            _staticEditableOverride, _editabilityHost?._staticEditable, DeclaredPageEditable, _page?.LookupMode == true);

    /// <summary>
    /// Bind a subpage part to its host for editability. Deliberately does NOT touch _opened:
    /// that flag drives BC's Open/Close guards, and a part is opened and closed with its host.
    /// </summary>
    internal void MarkPartOf(LiveNavTestPage host) => _editabilityHost = host;

    /// <summary>Run the page's OnOpenPage — see RunnerTestPageState.MarkOpened.</summary>
    internal void RaiseOnOpenPage() => _page?.RaiseOnOpenPage();

    /// <summary>
    /// Reach every subpage PART this page declares, the way <see cref="RunnerTestPageState.MarkOpened"/>
    /// calls it: right after the host's own OnOpenPage, before the host's first row is found.
    /// Issue #2677, corpus PR StefanMaron/BusinessCentral.AL.Language.Tests#141 — real BC
    /// materialises a page's declared FactBoxes as part of opening, with nothing in the
    /// host's own AL ever referencing <c>CurrPage.&lt;part&gt;</c>. <c>GetPart</c> raises the
    /// part's OWN OnOpenPage and, via <c>ReloadLinkedRow</c>, attempts its initial row load —
    /// which finds nothing yet (the host's own record is not positioned until MoveFirst runs
    /// right after this returns), so the part's OnAfterGetRecord/OnAfterGetCurrRecord fires
    /// for the first time from <see cref="Loaded"/>'s own refresh once the host DOES have a
    /// row, not from here.
    ///
    /// Each control is isolated in its own try/catch: a part the runner cannot build (a
    /// precompiled Base App page the runner has no metadata for, an unsupported shape) must
    /// not prevent the HOST from opening, or every card carrying one unbuildable FactBox
    /// would refuse OpenView entirely. An AL test that genuinely touches such a part still
    /// gets the normal named refusal through <see cref="GetPart"/> — this only skips the
    /// EAGER attempt, it does not swallow the refusal a real touch would raise.
    /// </summary>
    internal void EagerlyBuildParts()
    {
        if (_page == null) return;
        foreach (var controlId in _page.AllPartControlIds())
        {
            try { GetPart(controlId); }
            catch (Exception ex)
            {
                if (Environment.GetEnvironmentVariable("AL_RUNNER_TRACE_PAGE_METADATA") == "1")
                    Console.Out.WriteLine($"[MockTestPage.EagerlyBuildParts] control {controlId} on page {_pageId}: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    public override bool IsOpened() => _opened;
}
