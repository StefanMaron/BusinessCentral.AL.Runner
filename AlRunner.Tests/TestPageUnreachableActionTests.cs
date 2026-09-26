// TestPageUnreachableActionTests — a page's own action on a TestPage the runner built no AL
// page object for refuses on Invoke() instead of returning as though it ran (#4684).
//
// Runner-side mechanism test: each path fires when something the RUNNER owns is missing (no
// compiled page type, no captured metadata), which no AL statement can arrange, so no corpus
// or runner-extras bundle can drive it. The built-in actions are pinned unchanged beside it.

using AlRunner;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Types;
using Xunit;

namespace AlRunner.Tests;

// GetPageExtensionIdsForPage reads RecordPatches' parsed-page statics.
[Collection(RecordPatchesSerialCollection.Name)]
public sealed class TestPageUnreachableActionTests
{
    // An id no fixture declares, so no pageextension can own an action on it.
    private const int PageId = 88468401;
    private const int ActionId = 7;

    private static void AssertRefusal(RunnerOutOfScopeException ex, int pageId)
    {
        Assert.Equal($"TestPage action {ActionId} on page {pageId}", ex.Api);
        Assert.StartsWith("not-yet-implemented — testpage-action: ", ex.Reason);
        Assert.Contains("OnAction trigger cannot be reached", ex.Reason);
        Assert.Contains("docs/limitations.md#testpage-shape-gaps", ex.Message);
    }

    /// <summary>Path 1: CodeunitPatches' raw navigation mock.</summary>
    [Fact]
    public void NavigationMock_OwnAction_RefusesNamingPageAndAction()
    {
        var page = new MockITestPage { DiagnosticPageId = PageId };
        var action = page.GetAction(ActionId);

        var ex = Assert.Throws<RunnerOutOfScopeException>(() => action.Invoke());
        AssertRefusal(ex, PageId);
    }

    /// <summary>Path 2: LiveNavTestPage with no AL page object and no owning pageextension.</summary>
    [Fact]
    public void LivePageWithoutPageObject_OwnAction_Refuses()
    {
        var page = new LiveNavTestPage(null, new Dictionary<int, int>(), creatable: false,
            page: null, owner: null, pageId: PageId);
        var action = page.GetAction(ActionId);

        var ex = Assert.Throws<RunnerOutOfScopeException>(() => action.Invoke());
        AssertRefusal(ex, PageId);
    }

    /// <summary>Path 3: ExtensionOnlyTestAction when no compiled pageextension owns the id.</summary>
    [Fact]
    public void ExtensionOnlyAction_NoExtensionOwnsTheId_Refuses()
    {
        var page = new LiveNavTestPage(null, new Dictionary<int, int>(), creatable: false,
            page: null, owner: null, pageId: PageId);
        var action = new ExtensionOnlyTestAction(page, new object(), null!, PageId, ActionId);

        var ex = Assert.Throws<RunnerOutOfScopeException>(() => action.Invoke());
        AssertRefusal(ex, PageId);
    }

    /// <summary>
    /// Built-in actions keep the no-op mock: callers of OK/Cancel/Edit/View on a mock page may
    /// rely on them returning, and whether they should refuse is a separate question (#4684).
    /// </summary>
    [Fact]
    public void NavigationMock_BuiltInActions_StillReturn()
    {
        var page = new MockITestPage { DiagnosticPageId = PageId };
        page.GetBuiltInAction(FormResult.OK).Invoke();
        page.GetBuiltInAction(FormResult.Cancel).Invoke();
        page.Edit().Invoke();
        page.View().Invoke();
        Assert.IsType<MockITestAction>(page.GetBuiltInAction(FormResult.OK));
    }

    /// <summary>Reading the refusing action's state does not refuse — only running it does.</summary>
    [Fact]
    public void NavigationMock_OwnAction_VisibleAndEnabledStillAnswer()
    {
        var action = new MockITestPage { DiagnosticPageId = PageId }.GetAction(ActionId);
        Assert.True(action.Visible);
        Assert.True(action.Enabled);
    }
}
