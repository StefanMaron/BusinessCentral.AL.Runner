// RequestPageActionDispatchTests — the runner-internal half of #2457.
//
// The BC-behaviour claim (a [RequestPageHandler] invoking a request-page action gets BC's
// "The action with ID = <id> is not found on the page." and the OnAction does not run) is
// measured upstream: corpus codeunit 60399 "Test Report ReqPage Action".
//
// Pinned HERE: RequestPageTestPage.GetAction answers null, which is what makes BC's own
// NavTestPageBase.GetAction raise NavTestActionNotFoundException. It never hands back the base
// mock's MockITestAction, whose Invoke is empty, and it never runs the form's OnAction method.
using AlRunner;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

[Collection(RecordPatchesSerialCollection.Name)]
public sealed class RequestPageActionDispatchTests
{
    private const int ReportId = 92457;

    // Shaped like the AL compiler's request-page class: one method per action trigger, named
    // <Action>_a<n>_OnAction, whose member id hashes from the REPORT's id.
    public sealed class FakeRequestPageForm
    {
        public int StampRuns;
        public void StampText_a45_OnAction() => StampRuns++;
    }

    [Fact]
    public void GetAction_ForADeclaredAction_AnswersNull_AndRunsNoTrigger()
    {
        var form = new FakeRequestPageForm();
        var page = RequestPageTestPage.Bind(form, new object(), ReportId, offersOk: true);

        var action = page.GetAction(RunnerPageInstance.MemberId(ReportId, "StampText"));

        Assert.Null(action);
        Assert.Equal(0, form.StampRuns);
    }

    [Fact]
    public void GetAction_ForAnUnknownAction_AnswersNull_NotTheEmptyMock()
    {
        var page = RequestPageTestPage.Bind(new FakeRequestPageForm(), new object(), ReportId, offersOk: true);

        Assert.Null(page.GetAction(RunnerPageInstance.MemberId(ReportId, "NoSuchAction")));
    }
}
