// RequestPageOpenedByBcBindingTests — the runner-internal half of #4067.
//
// Precompiled AL calls NavReport.RunAsync(NavSession, int); BC's own report engine then opens
// the request page and hands it to RunnerTestClientSession.GetPage without the runner having
// bound it. GetPage now binds such a form through NavReportSync.BindRequestPageOpenedByBc
// instead of building a page surface for the REPORT's id.
//
// The BC-behaviour claim (a [RequestPageHandler] reached from Base Application code gets the
// request page and can Cancel it) is measured upstream: corpus codeunit 60037
// "Rpt Run Precompiled Caller" (StefanMaron/BusinessCentral.AL.Language.Tests#342).
//
// Pinned HERE: how the binding finds the report (Parent, else the AL compiler's CurrReport
// field, which is where BC-emitted request pages keep it), that the id comes from that report,
// that the binding is the one GetPage's TryGetFor answers afterwards, and that a form tied to
// no report is refused loudly rather than handed some page.
using System;
using AlRunner;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// IsProcessingOnly reads RecordPatches' parse statics.
[Collection(RecordPatchesSerialCollection.Name)]
public sealed class RequestPageOpenedByBcBindingTests
{
    // Stand-ins shaped like the AL compiler's output: a report type named Report<N> deriving
    // from a type named NavReport, and a nested request page holding it in CurrReport.
    public class NavReport { }
    public sealed class Report94067 : NavReport { }
    public sealed class Report94068 : NavReport { }
    public sealed class NotAReport { }

    public sealed class FakeRequestPage
    {
        public object? CurrReport;
    }

    [Fact]
    public void ParentIsNull_BindsToTheReportInCurrReport()
    {
        var form = new FakeRequestPage { CurrReport = new Report94067() };

        var page = NavReportSync.BindRequestPageOpenedByBc(form, parent: null);

        Assert.Equal(94067, page.PageId);
        Assert.Same(page, RequestPageTestPage.TryGetFor(form));
    }

    [Fact]
    public void ParentIsANavReport_WinsOverCurrReport()
    {
        var form = new FakeRequestPage { CurrReport = new Report94068() };

        var page = NavReportSync.BindRequestPageOpenedByBc(form, parent: new Report94067());

        Assert.Equal(94067, page.PageId);
    }

    [Fact]
    public void NoReportAnywhere_IsRefusedByName_NotHandedAPage()
    {
        var form = new FakeRequestPage { CurrReport = new NotAReport() };

        var ex = Assert.Throws<RunnerOutOfScopeException>(
            () => NavReportSync.BindRequestPageOpenedByBc(form, parent: null));

        Assert.Contains("request-page-report", ex.Message, StringComparison.Ordinal);
        Assert.Contains(typeof(NotAReport).FullName!, ex.Message, StringComparison.Ordinal);
        Assert.Null(RequestPageTestPage.TryGetFor(form));
    }
}
