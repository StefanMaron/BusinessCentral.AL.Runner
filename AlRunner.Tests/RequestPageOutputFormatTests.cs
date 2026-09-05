// RequestPageOutputFormatTests — issue #2887.
//
// A [RequestPageHandler] closes the request page by asking for an OUTPUT, and which output it
// asked for decides whether the runner may answer with a dataset. BC's own
// ReportResultSetProcessorFactory.GetTestResultProcessor (bc281, decompiled) branches on that
// first:
//
//     NavReportFormat f = Parameters.ReportTargetFormat;
//     if (f != NavReportFormat.None && f != NavReportFormat.Xml)
//     {
//         ...park the file name on Parameters, flip Download -> Save, clear the three
//            TestExecution fields...
//         return null;                       // no test processor: the real renderer runs
//     }
//     return new ReportSaveAsXmlRenderer(...);
//
// The runner's stand-in implemented only the second half — any parked output file name got the
// XML dataset renderer. Every ALSaveAs* parks one (NavTestPage.ALSaveAsExcel sets
// ReportOutputFileName, ReportOutputFormat = FormResult.Excel, then invokes the built-in Excel
// action), so a handler calling SaveAsExcel was answered with an XML dataset written into the
// .xlsx path it named, the run reported success, and the rendering refusal was skipped because
// a dataset had been "written". Six Tests-SINGLESERVER tests in Codeunit134335 then failed
// inside the toolkit's OpenXml reader rather than at the unsupported call.
//
// The end-to-end proof that the refusal is now reached is the AL test
// RSS Tests.RequestPageSaveAsExcel_RefusesRenderingLoudly in
// tests/runner-extras/standalone-suites/report-saveas-stream. What is pinned HERE is the rule
// itself — including that it fails CLOSED, which no AL test can show because it would need a
// FormResult value BC does not have yet.
using System;
using System.Reflection;
using AlRunner;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

public sealed class RequestPageOutputFormatTests
{
    [Theory]
    [InlineData("Xml")]     // TestRequestPage.SaveAsXml — the dataset shape MS's report tests use
    [InlineData("None")]    // BC's own rule admits None as well; kept identical to it
    public void DatasetFormats_TakeTheDatasetRenderer(string formResult)
    {
        Assert.True(NavReportSync.RequestedOutputIsDataset(formResult),
            $"FormResult.{formResult} must still be answered with the dataset renderer — "
            + "this is the path every [RequestPageHandler] using SaveAsXml depends on.");
    }

    [Theory]
    [InlineData("Excel")]         // the defect in #2887
    [InlineData("ExcelDataset")]
    [InlineData("Word")]
    [InlineData("Pdf")]
    [InlineData("Print")]
    [InlineData("PreviewPrint")]
    [InlineData("Preview")]
    [InlineData("Schedule")]
    public void RenderedArtifactFormats_DoNotTakeTheDatasetRenderer(string formResult)
    {
        Assert.False(NavReportSync.RequestedOutputIsDataset(formResult),
            $"FormResult.{formResult} asks for a rendered artifact. Answering it with the XML "
            + "dataset renderer writes a dataset into the file the caller named for something "
            + "else and skips the rendering refusal — the silent wrong answer #2887 is about.");
    }

    /// <summary>
    /// Fail closed. A FormResult value this rule has never seen — a BC build that adds or
    /// renames one — must NOT be answered with a dataset. Getting this backwards is how the
    /// defect would come back: the old code effectively treated every value as "dataset".
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("Xlsx")]
    [InlineData("XML")]          // case matters: the value is compared as BC spells it
    [InlineData("SomeFutureFormat")]
    [InlineData(null)]
    public void UnknownFormat_FailsClosed(string? formResult)
    {
        Assert.False(NavReportSync.RequestedOutputIsDataset(formResult),
            "an unrecognised FormResult must fall through to the rendering path, which refuses "
            + "loudly, rather than be silently answered with a dataset.");
    }
}

/// <summary>
/// The rot guard for the rule above. It compares FormResult NAMES, so a BC build that renames
/// or drops one would make every request either refuse or dataset without a single test
/// failing — the same shape as the async-flavour rot #2734 was about.
/// </summary>
[Collection(BcEngineCollection.Name)]
public sealed class RequestPageOutputFormatBcMembersTests
{
    private readonly BcEngineFixture _engine;
    public RequestPageOutputFormatBcMembersTests(BcEngineFixture engine) => _engine = engine;

    [SkippableFact]
    public void Ncl_StillDeclares_TheFormResultNames_TheRuleMatchesOn()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var types = typeof(ITreeObject).Assembly.GetType("Microsoft.Dynamics.Nav.Types.FormResult")
            ?? AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("Microsoft.Dynamics.Nav.Types.FormResult"))
                .FirstOrDefault(t => t != null);
        Assert.True(types != null,
            "Microsoft.Dynamics.Nav.Types.FormResult is gone — NavReportSync compares this enum's "
            + "member names by string, so a rename would silently change which requests get a dataset.");

        foreach (var name in new[] { "None", "Xml", "Excel" })
            Assert.True(Enum.IsDefined(types!, name),
                $"FormResult.{name} is gone from this BC build. RequestedOutputIsDataset matches on "
                + "these exact names; losing one silently moves a request to the other branch.");

        // And the TestExecution fields the handler parks its request on.
        var testExecution = typeof(ITreeObject).Assembly
            .GetType("Microsoft.Dynamics.Nav.Runtime.NavTestExecution");
        Assert.True(testExecution != null, "NavTestExecution is gone from this Ncl build.");
        const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (var prop in new[] { "ReportOutputFileName", "ReportParameterOutputFileName", "ReportOutputFormat" })
            Assert.True(testExecution!.GetProperty(prop, F) != null,
                $"NavTestExecution.{prop} is gone — TryCreateTestDatasetProcessor reads the requested "
                + "output from it, and now throws rather than guessing when it cannot.");
    }
}
