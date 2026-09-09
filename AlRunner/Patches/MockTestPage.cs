// MockTestPage — lightweight ITestPage / ITestField / ITestAction implementations
// for the runner's NavTestPage vtable fix.
//
// NavTestPageHandle_CreateTarget constructs a real NavTestPage via its internal
// 3-arg ctor passing a MockITestPage as the ITestPage.  Cecil IL rewrites in
// NclCecilRewrite ensure the runtime never calls out to the real TestPageClient
// or TestClientProxy.Proxy, so these mocks only need to satisfy the direct method
// calls NavTestPageBase.GetField / GetAction / GetDataItem make into them.
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

internal partial class LiveNavTestPage : MockITestPage
{
    // Null for a page with no SourceTable (issue #2007) — a legal AL shape (StandardDialog
    // pickers/prompts bound to page globals). Every member that genuinely needs a row goes
    // through RequireRecord, which turns a would-be NRE into a named, loud refusal instead of
    // silently doing nothing; page-variable-bound field access never reaches here at all.
    private readonly NavRecord? _record;
    private readonly IReadOnlyDictionary<int, int> _controlIdToFieldNo;
    private readonly Dictionary<int, LiveNavTestField> _fields = new();
    private readonly Dictionary<int, PageVariableTestField> _pageVariableFields = new();
    private readonly bool _creatable;
    // The live AL page object, when the runner could build one. Null for a page it did not
    // compile (no metadata to build a control tree from) — then only Rec-bound controls
    // resolve, which is all this class could ever do before.
    private readonly RunnerPageInstance? _page;

    // The ITreeObject every NavRecord on this page is constructed under, and the page's own
    // id — both needed to build a subpage part, which is another page over another table.
    private readonly object? _owner;
    private readonly int _pageId;
    private readonly Dictionary<int, ITestPart> _parts = new();

    public LiveNavTestPage(NavRecord? record, IReadOnlyDictionary<int, int> controlIdToFieldNo)
        : this(record, controlIdToFieldNo, creatable: true, page: null) { }

    public LiveNavTestPage(NavRecord? record, IReadOnlyDictionary<int, int> controlIdToFieldNo, bool creatable)
        : this(record, controlIdToFieldNo, creatable, page: null) { }

    public LiveNavTestPage(NavRecord? record, IReadOnlyDictionary<int, int> controlIdToFieldNo, bool creatable,
        RunnerPageInstance? page)
        : this(record, controlIdToFieldNo, creatable, page, owner: null, pageId: 0) { }

    public LiveNavTestPage(NavRecord? record, IReadOnlyDictionary<int, int> controlIdToFieldNo, bool creatable,
        RunnerPageInstance? page, object? owner, int pageId)
    {
        _record = record;
        _controlIdToFieldNo = controlIdToFieldNo;
        _creatable = creatable;
        _page = page;
        _owner = owner;
        _pageId = pageId;
    }

    internal NavRecord? Record => _record;

    // The page object a SUBCLASS needs: LiveNavTestPart re-positions a linked part through the
    // host page's own OnFindRecord (issue #3439), and _page itself is private.
    private protected RunnerPageInstance? PageInstance => _page;

    /// <summary>
    /// The record this operation genuinely needs, or a loud, named refusal instead of an NRE
    /// when the page has none (issue #2007: a page with no SourceTable — the StandardDialog
    /// shape — is legal AL, and only Rec-dependent members are affected; page-variable-bound
    /// field access resolves entirely through RunnerPageInstance's source-expression table and
    /// never calls this).
    /// </summary>
    protected internal NavRecord RequireRecord(string what)
        => _tornDown ? throw MakeTestPageNotOpenException()
        // The api carries no " — ": OutOfScopeMessage.TryParse cuts the api from the reason at
        // the FIRST one, so an api that spells the separator itself makes the untyped recovery
        // path report "TestPage page 60100" with "New() — testpage-modal-no-source-table — …"
        // as the reason. Same defect #2945 fixed for Feature Key Modify (#2999).
        : _record ?? throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
            $"TestPage page {_pageId} ({what})",
            "testpage-modal-no-source-table — this page has no SourceTable, so there is no "
            + "record-backed rowset for this operation. Controls bound to page variables are "
            + "supported; row navigation, filtering, Insert/Modify and Rec-bound field access "
            + "are not, because there is no record to act on. See docs/scope.md");

    // BC reports these in NavInsertDeniedPermissionException and friends. Answering 0/""
    // (the mock's values) is what produced "Insert is not allowed. Page = , Id = 0" — an
    // error that named no page at all.
    public override int PageId => _pageId;

    // TestPage.Editable() reaches here (NavTestPage.ALEditable => TestPage.RuntimeEditable).
    // A constant true made every `CurrPage.Editable(false)` invisible to the test that was
    // written to check it.
    public override bool RuntimeEditable => _staticEditable;

    // TestPage.Caption() (#1776). The base mock answered a constant empty string, which was
    // wrong for BOTH of a page's caption sources: the static `Caption = '…'` property AND a
    // runtime `CurrPage.Caption := '…'` assignment made from OnOpenPage. Both write the same
    // underlying NavForm.PageCaption — reading it here is what makes a single accessor answer
    // correctly whether or not the page ever touched CurrPage.Caption at all.
    public override string Caption => _page?.PageCaption ?? string.Empty;

    // BC's NavTestPageBase.New() consults Creatable before inserting. The base mock returns
    // false (it has no backing record to insert into), but a LIVE test page does — so the
    // answer must come from the page's declared InsertAllowed rather than a hardcoded false,
    // which denied every TestPage.New() regardless of the page under test.
    public override bool Creatable => _creatable;

}
